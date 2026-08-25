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
}
