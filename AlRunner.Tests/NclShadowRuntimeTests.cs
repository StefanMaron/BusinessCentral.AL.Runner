// NclShadowRuntimeTests — pins the mechanism the tool package now relies on to avoid
// shipping Microsoft.Dynamics.Nav.Ncl.dll (see check-nupkg-contents.sh, which asserts
// its absence from the packed nupkg with no allow-list exception).
//
// CoreCLR's trusted-platform-assemblies (TPA) list is computed once, by the native host,
// before any of our own code runs — so a THIS-process fix once Ncl.dll is already known
// to be missing is impossible. NclShadowRuntime instead builds a "shadow" directory that
// mirrors the real install (so a fresh child process's TPA legitimately includes a real,
// on-disk Ncl.dll) and Program.cs re-execs into it via the dotnet muxer.
//
// The one entry this file exists to prove: MirrorInstallDirectory MUST copy the entry
// assembly (al-runner.dll) and its deps/runtimeconfig manifests as REAL, independent
// files rather than symlinks. This is not a style preference — it is a regression pin.
// Confirmed empirically while building this class: when the entry assembly in the
// shadow dir was a symlink back to the real install, CoreCLR reported
// AppContext.BaseDirectory as the symlink's TARGET directory (the real install), not the
// directory the symlink itself lived in (the shadow dir) — silently defeating the whole
// mechanism. Concretely, that made Program.cs's later "Cecil-rewrite Ncl.dll IN-PLACE"
// step write Ncl.dll back into the real install directory — the very directory
// check-nupkg-contents.sh's regression this whole class exists to prevent staying clean.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclShadowRuntimeTests
{
    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ncl-shadow-tests-{label}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool IsSymlink(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    // ─────────────────────────────────────────────────────────────────────────
    // NeedsShadow
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive: Ncl.dll present beside the running assembly (a package that
    /// still ships it, or a shadow child that already has its own real copy) means no
    /// shadow/re-exec dance is needed.</summary>
    [Fact]
    public void NeedsShadow_NclPresent_ReturnsFalse()
    {
        var dir = NewTempDir("needs-present");
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Microsoft.Dynamics.Nav.Ncl.dll"), new byte[] { 1, 2, 3 });
            Assert.False(NclShadowRuntime.NeedsShadow(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Negative: an install directory without Ncl.dll (the packaging fix's whole
    /// point — see check-nupkg-contents.sh) must be flagged as needing the shadow dance,
    /// specifically so Program.cs doesn't fall through to loading the raw, un-rewritten
    /// copy via the ALC.Resolving fallback (which crashes at startup on Linux — see the
    /// class doc on NclShadowRuntime).</summary>
    [Fact]
    public void NeedsShadow_NclAbsent_ReturnsTrue()
    {
        var dir = NewTempDir("needs-absent");
        try
        {
            File.WriteAllText(Path.Combine(dir, "al-runner.dll"), "not actually a dll, just needs to exist");
            Assert.True(NclShadowRuntime.NeedsShadow(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MirrorInstallDirectory — the entry-assembly-must-be-a-real-copy regression pin
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive + the actual regression pin: the entry assembly and its
    /// manifests land in the shadow dir as REAL files (not symlinks) with byte-identical
    /// content to the source. A symlink here would make CoreCLR report
    /// AppContext.BaseDirectory as the ORIGINAL install directory instead of the shadow
    /// dir — see this file's header comment for the empirical confirmation.</summary>
    [Fact]
    public void MirrorInstallDirectory_EntryAssemblyAndManifests_AreRealCopiesNotSymlinks()
    {
        var origDir = NewTempDir("mirror-orig");
        var shadowDir = NewTempDir("mirror-shadow");
        try
        {
            var dllBytes = new byte[] { 0x4D, 0x5A, 9, 9, 9 };
            File.WriteAllBytes(Path.Combine(origDir, "al-runner.dll"), dllBytes);
            File.WriteAllText(Path.Combine(origDir, "al-runner.deps.json"), "{\"deps\":true}");
            File.WriteAllText(Path.Combine(origDir, "al-runner.runtimeconfig.json"), "{\"rc\":true}");

            NclShadowRuntime.MirrorInstallDirectory(origDir, shadowDir);

            var shadowDll = Path.Combine(shadowDir, "al-runner.dll");
            var shadowDeps = Path.Combine(shadowDir, "al-runner.deps.json");
            var shadowRc = Path.Combine(shadowDir, "al-runner.runtimeconfig.json");

            Assert.False(IsSymlink(shadowDll), "al-runner.dll must be a real copy, not a symlink");
            Assert.False(IsSymlink(shadowDeps), "al-runner.deps.json must be a real copy, not a symlink");
            Assert.False(IsSymlink(shadowRc), "al-runner.runtimeconfig.json must be a real copy, not a symlink");

            Assert.Equal(dllBytes, File.ReadAllBytes(shadowDll));
            Assert.Equal("{\"deps\":true}", File.ReadAllText(shadowDeps));
            Assert.Equal("{\"rc\":true}", File.ReadAllText(shadowRc));

            // Independent inode, not just independent path: mutating the source after
            // mirroring must NOT change the shadow copy (a hardlink or symlink would
            // both fail this — only a genuine File.Copy passes).
            File.WriteAllBytes(Path.Combine(origDir, "al-runner.dll"), new byte[] { 0xFF, 0xFF });
            Assert.Equal(dllBytes, File.ReadAllBytes(shadowDll));
        }
        finally
        {
            Directory.Delete(origDir, recursive: true);
            Directory.Delete(shadowDir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // #2166: load-by-path assemblies (AlRunner.QueryJoin.dll, AlRunner.Provisioning.dll)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive + the #2166 regression pin: AlRunner.QueryJoin.dll and
    /// AlRunner.Provisioning.dll must land in the shadow dir as REAL, independent copies,
    /// not symlinks back to the original install directory.
    ///
    /// Before this fix they fell into the generic "every other dependency DLL" bucket
    /// (see <see cref="MirrorInstallDirectory_OtherDependencyDll_IsSymlinkedNotCopied"/>)
    /// and were symlinked. That happened to keep working ONLY because
    /// <see cref="EnsureShadowDir"/>'s caller always passes the top-level "any" install
    /// directory as origDir — which does ship both files — so the symlink target
    /// resolved. But nothing re-validates that symlink once the shadow dir is cached:
    /// EnsureShadowDir's "reusable" check only looks at the marker file plus the entry
    /// assembly and Ncl.dll, never at every other file it once linked. If the file the
    /// symlink points to is later removed from the original install (a tool
    /// reinstall/upgrade in place, `mise`/`dotnet tool` pruning an old version, disk
    /// cleanup) the shadow dir is still reported "reusable", and the dangling symlink
    /// only surfaces when <c>RecordPatches.EnsureJoinExecutorLoaded</c> lazily
    /// <c>Assembly.LoadFrom</c>s it on the FIRST multi-dataitem query JOIN test that
    /// actually runs — reproduced empirically against the published 2.8.0 package: a
    /// shadow dir built while the install was intact keeps working right up until the
    /// original install's AlRunner.QueryJoin.dll is deleted, at which point every JOIN
    /// test in that process starts failing with exactly the FileNotFoundException #2166
    /// reports, even though nothing about the shadow dir itself changed.
    ///
    /// AlRunner.Provisioning.dll is resolved differently (implicit CLR probing off a
    /// compiled-in reference, not an explicit LoadFrom — see ProvisioningCheck.cs) but
    /// off the exact same AppContext.BaseDirectory, post-shadow-hop, so it inherits the
    /// identical fragility and gets the identical fix.</summary>
    [Fact]
    public void MirrorInstallDirectory_LoadByPathAssemblies_AreRealCopiesNotSymlinks()
    {
        var origDir = NewTempDir("mirror-loadbypath-orig");
        var shadowDir = NewTempDir("mirror-loadbypath-shadow");
        try
        {
            var queryJoinBytes = new byte[] { 0x4D, 0x5A, 1, 2, 3 };
            var provisioningDllBytes = new byte[] { 0x4D, 0x5A, 4, 5, 6 };
            var provisioningPdbBytes = new byte[] { 7, 8, 9 };
            File.WriteAllBytes(Path.Combine(origDir, "AlRunner.QueryJoin.dll"), queryJoinBytes);
            File.WriteAllBytes(Path.Combine(origDir, "AlRunner.Provisioning.dll"), provisioningDllBytes);
            File.WriteAllBytes(Path.Combine(origDir, "AlRunner.Provisioning.pdb"), provisioningPdbBytes);

            NclShadowRuntime.MirrorInstallDirectory(origDir, shadowDir);

            var shadowQueryJoin = Path.Combine(shadowDir, "AlRunner.QueryJoin.dll");
            var shadowProvisioningDll = Path.Combine(shadowDir, "AlRunner.Provisioning.dll");
            var shadowProvisioningPdb = Path.Combine(shadowDir, "AlRunner.Provisioning.pdb");

            Assert.False(IsSymlink(shadowQueryJoin), "AlRunner.QueryJoin.dll must be a real copy, not a symlink");
            Assert.False(IsSymlink(shadowProvisioningDll), "AlRunner.Provisioning.dll must be a real copy, not a symlink");
            Assert.False(IsSymlink(shadowProvisioningPdb), "AlRunner.Provisioning.pdb must be a real copy, not a symlink");

            // Independent inode, not just independent path: deleting the source AFTER
            // mirroring must not take the shadow copy down with it — that is the exact
            // failure mode #2166 reports (a dangling symlink surfacing as
            // FileNotFoundException deep inside a lazily-loaded join test).
            File.Delete(Path.Combine(origDir, "AlRunner.QueryJoin.dll"));
            File.Delete(Path.Combine(origDir, "AlRunner.Provisioning.dll"));

            Assert.True(File.Exists(shadowQueryJoin), "shadow copy must survive the original install's file being removed");
            Assert.Equal(queryJoinBytes, File.ReadAllBytes(shadowQueryJoin));
            Assert.True(File.Exists(shadowProvisioningDll), "shadow copy must survive the original install's file being removed");
            Assert.Equal(provisioningDllBytes, File.ReadAllBytes(shadowProvisioningDll));
        }
        finally
        {
            Directory.Delete(origDir, recursive: true);
            Directory.Delete(shadowDir, recursive: true);
        }
    }

    /// <summary>Negative (the cost-control half of the same design): every OTHER file —
    /// the large, numerous dependency DLLs that are NOT the entry assembly or its
    /// manifests — must be linked, not copied, so building/rebuilding the shadow dir
    /// stays near-zero-cost. Asserts a symlink specifically (not just "resolves to the
    /// right content", which a copy would also satisfy) so a regression that silently
    /// switches everything to File.Copy — correct but expensive — still fails this test.</summary>
    [Fact]
    public void MirrorInstallDirectory_OtherDependencyDll_IsSymlinkedNotCopied()
    {
        var origDir = NewTempDir("mirror-orig2");
        var shadowDir = NewTempDir("mirror-shadow2");
        try
        {
            var depBytes = new byte[] { 0x4D, 0x5A, 7, 7, 7 };
            var depPath = Path.Combine(origDir, "Some.Dependency.dll");
            File.WriteAllBytes(depPath, depBytes);

            NclShadowRuntime.MirrorInstallDirectory(origDir, shadowDir);

            var shadowDep = Path.Combine(shadowDir, "Some.Dependency.dll");
            Assert.True(IsSymlink(shadowDep), "non-entry dependency DLLs should be symlinked for near-zero cost");
            Assert.Equal(depBytes, File.ReadAllBytes(shadowDep));
        }
        finally
        {
            Directory.Delete(origDir, recursive: true);
            Directory.Delete(shadowDir, recursive: true);
        }
    }

    /// <summary>Positive: a subdirectory (e.g. a satellite-resource culture folder, or
    /// runtimes/) is mirrored too, as a directory symlink, and its content is reachable
    /// through it.</summary>
    [Fact]
    public void MirrorInstallDirectory_Subdirectory_IsMirroredAndReachable()
    {
        var origDir = NewTempDir("mirror-orig3");
        var shadowDir = NewTempDir("mirror-shadow3");
        try
        {
            var subDir = Path.Combine(origDir, "cs");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "al-runner.resources.dll"), "fake satellite resource");

            NclShadowRuntime.MirrorInstallDirectory(origDir, shadowDir);

            var shadowSubFile = Path.Combine(shadowDir, "cs", "al-runner.resources.dll");
            Assert.True(File.Exists(shadowSubFile));
            Assert.Equal("fake satellite resource", File.ReadAllText(shadowSubFile));
        }
        finally
        {
            Directory.Delete(origDir, recursive: true);
            Directory.Delete(shadowDir, recursive: true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // entrySource — per-BC-minor engine variant swap (#2024 item 3 / #2027)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive: when entrySource is given, the entry assembly (and its
    /// manifests) come from THAT directory instead of origDir — proves the actual
    /// variant-swap mechanism, not just "some file got copied". Every OTHER file still
    /// comes from origDir (the shared dependency closure), asserted via the untouched
    /// dependency DLL.</summary>
    [Fact]
    public void MirrorInstallDirectory_WithEntrySource_EntryAssemblyComesFromEntrySource()
    {
        var origDir = NewTempDir("mirror-swap-orig");
        var entryDir = NewTempDir("mirror-swap-entry");
        var shadowDir = NewTempDir("mirror-swap-shadow");
        try
        {
            var origBytes = new byte[] { 0x4D, 0x5A, 1, 1, 1 };
            var variantBytes = new byte[] { 0x4D, 0x5A, 2, 2, 2 };
            File.WriteAllBytes(Path.Combine(origDir, "al-runner.dll"), origBytes);
            File.WriteAllText(Path.Combine(origDir, "al-runner.deps.json"), "{\"from\":\"orig\"}");
            File.WriteAllBytes(Path.Combine(origDir, "Some.Dependency.dll"), new byte[] { 9, 9 });

            File.WriteAllBytes(Path.Combine(entryDir, "al-runner.dll"), variantBytes);
            File.WriteAllText(Path.Combine(entryDir, "al-runner.deps.json"), "{\"from\":\"variant\"}");

            NclShadowRuntime.MirrorInstallDirectory(origDir, shadowDir, entryDir);

            // The entry assembly and its present manifest came from entryDir, not origDir.
            Assert.Equal(variantBytes, File.ReadAllBytes(Path.Combine(shadowDir, "al-runner.dll")));
            Assert.Equal("{\"from\":\"variant\"}", File.ReadAllText(Path.Combine(shadowDir, "al-runner.deps.json")));
            Assert.False(IsSymlink(Path.Combine(shadowDir, "al-runner.dll")));

            // The shared dependency closure still comes from origDir, untouched.
            var shadowDep = Path.Combine(shadowDir, "Some.Dependency.dll");
            Assert.True(IsSymlink(shadowDep));
            Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(shadowDep));
        }
        finally
        {
            Directory.Delete(origDir, recursive: true);
            Directory.Delete(entryDir, recursive: true);
            Directory.Delete(shadowDir, recursive: true);
        }
    }

    /// <summary>Negative: a MustCopyNames file entrySource does NOT have (e.g. a variant
    /// built without al-runner.runtimeconfig.dev.json, which only a plain `dotnet build`
    /// produces) falls back to origDir's copy instead of leaving the shadow dir missing
    /// that file.</summary>
    [Fact]
    public void MirrorInstallDirectory_WithEntrySource_MissingManifestFallsBackToOrigDir()
    {
        var origDir = NewTempDir("mirror-swap-fallback-orig");
        var entryDir = NewTempDir("mirror-swap-fallback-entry");
        var shadowDir = NewTempDir("mirror-swap-fallback-shadow");
        try
        {
            File.WriteAllBytes(Path.Combine(origDir, "al-runner.dll"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(origDir, "al-runner.runtimeconfig.dev.json"), "{\"dev\":true}");
            // entryDir has NO al-runner.runtimeconfig.dev.json at all.
            File.WriteAllBytes(Path.Combine(entryDir, "al-runner.dll"), new byte[] { 2 });

            NclShadowRuntime.MirrorInstallDirectory(origDir, shadowDir, entryDir);

            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(shadowDir, "al-runner.dll")));
            Assert.Equal("{\"dev\":true}",
                File.ReadAllText(Path.Combine(shadowDir, "al-runner.runtimeconfig.dev.json")));
        }
        finally
        {
            Directory.Delete(origDir, recursive: true);
            Directory.Delete(entryDir, recursive: true);
            Directory.Delete(shadowDir, recursive: true);
        }
    }
}
