// PkgDedupStaleStageReuseTests — a pkgdedup stage must not be reused once an entry in it has
// stopped resolving (#2967).
//
// The defect
// ----------
// DeduplicateAppPackageDirs stages one symlink per picked .app under
// al-runner-pkgdedup/<key>/, where <key> is a hash of the picked set's absolute PATHS. The
// only gate on reusing an existing stage was `Directory.Exists(stage)`.
//
// Existence is a fine test of COMPLETENESS — a stage is published by a single
// Directory.Move, which is one rename(2), so the path either does not exist or names the
// fully populated directory (measured: 27,168 cross-process observations of a live publish,
// zero partial; see PkgDedupStaging's header). It is NOT a test of VALIDITY. The key
// addresses paths, not bytes, and each entry is a symlink, so the stage outlives the files it
// points at: remove a worktree, deinitialize a submodule, or let a test fixture's temp tree be
// reclaimed at its owner's exit, and the entry resolves to nothing while its directory still
// exists. Nothing prunes this root, so the state is permanent.
//
// It is also the state this machine is actually in. Measured 2026-09-06 with nine runners
// active: 138 stage directories, 28,004 staged entries (all symlinks), 455 of them dangling
// across 73 of the 138 stages — 53% of the shared cache holding at least one entry that
// resolves to nothing, and every dangling target's parent directory gone as well.
//
// Handing such a directory to BC's native package reader fails the run: it reports the
// unreadable package as `AL1023 ... is not valid`, and DeduplicateAppPackageDirs' own comment
// records that the error is attributed to the COMPILATION rather than to the package, so one
// bad entry fails every compile that scans the directory even when nothing references it.
//
// Test strategy
// -------------
// Drive the real DeduplicateAppPackageDirs by reflection (the same approach
// BcCompilerPkgDedupRelativePathTests uses — it is pure filesystem/zip logic and needs no BC
// runtime), let it publish a stage, then put that stage into the state measured above by
// replacing one entry with a dangling symlink. Ask for the identical package set again: the
// path set is unchanged, so the key is unchanged, so the stale stage is a reuse candidate.
//
// RED before the fix: the second call returns the poisoned stage and at least one .app in it
// does not resolve. GREEN after: the stage is rebuilt and every entry opens as a real package.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using Xunit;
using AlRunner;

namespace AlRunner.Tests;

public sealed class PkgDedupStaleStageReuseTests : IDisposable
{
    private readonly string _root;

    public PkgDedupStaleStageReuseTests()
    {
        _root = TestScratch.Dir("al-runner-pkgdedup-stale-tests");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void StageHoldingADanglingEntry_IsRebuiltRatherThanReused()
    {
        var packageDirs = TwoDirsForcingTheStagingBranch();

        var first = InvokeDeduplicateAppPackageDirs(packageDirs, excludeAppId: null);
        var stage = Assert.Single(first);
        Assert.NotEqual(packageDirs, first);              // the staging branch really engaged
        var staged = Directory.GetFiles(stage);
        Assert.NotEmpty(staged);

        // Put the stage into the state measured on this machine 455 times over: one entry
        // whose target no longer exists. Nothing about the CALLER changes — the package dirs
        // and therefore the content-addressed key are identical — so this stage is exactly
        // what the next compile with the same inputs will find under its key.
        var poisoned = staged[0];
        File.Delete(poisoned);
        File.CreateSymbolicLink(poisoned, Path.Combine(_root, "was-deleted-under-us.app"));
        AssertDoesNotOpen(poisoned, "fixture must actually be dangling to be a repro");

        var second = InvokeDeduplicateAppPackageDirs(packageDirs, excludeAppId: null);
        var reused = Assert.Single(second);

        var entries = Directory.GetFiles(reused);
        Assert.Equal(staged.Length, entries.Length);
        foreach (var entry in entries)
            AssertOpensAsNavxPackage(entry);
    }

    // Negative direction: an INTACT stage must still be reused as-is. The validity check
    // exists to catch a stale stage, not to defeat the deduplication cache — a check that
    // rebuilt every time would make this test pass while costing every concurrent runner the
    // staging work the shared directory exists to avoid.
    [Fact]
    public void StageWithEveryEntryResolving_IsReusedNotRebuilt()
    {
        var packageDirs = TwoDirsForcingTheStagingBranch();

        var first = Assert.Single(InvokeDeduplicateAppPackageDirs(packageDirs, excludeAppId: null));
        // A marker no rebuild would reproduce: if the directory is rebuilt, it is gone.
        var marker = Path.Combine(first, "reuse-marker.txt");
        File.WriteAllText(marker, "published-once");
        var createdAt = Directory.GetCreationTimeUtc(first);

        var second = Assert.Single(InvokeDeduplicateAppPackageDirs(packageDirs, excludeAppId: null));

        Assert.Equal(first, second);
        Assert.True(File.Exists(marker), "an intact stage must be adopted, not rebuilt");
        Assert.Equal(createdAt, Directory.GetCreationTimeUtc(second));
    }

    // ── PkgDedupStaging.Publish: the concurrent-runner half of the same defect ──────────
    //
    // Two runners can compute the same key at the same time. The loser's Publish finds the
    // winner's directory already there and adopts it. That is right when the winner's
    // directory is usable and wrong when it is stale — adopting then DISCARDS the good tmp
    // this process just staged and returns the poisoned directory instead, turning a healthy
    // compile into an AL1023.

    [Fact]
    public void Publish_ExistingStageIsStale_ReplacesItWithTheFreshlyStagedTmp()
    {
        var stage = Path.Combine(_root, "collide-key");
        Directory.CreateDirectory(stage);
        File.CreateSymbolicLink(Path.Combine(stage, "A.app"), Path.Combine(_root, "gone.app"));

        var tmp = stage + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, "A.app"), "staged-by-the-loser");

        var warn = new StringWriter();
        var used = AlRunner.Infrastructure.PkgDedupStaging.Publish(tmp, stage, warn);

        Assert.Equal(stage, used);
        Assert.Equal("staged-by-the-loser", File.ReadAllText(Path.Combine(used, "A.app")));
        // And it says so — silently swapping a shared cache entry would hide a real problem.
        Assert.Contains("[pkgdedup]", warn.ToString());
        Assert.Contains("no longer exists", warn.ToString());
        // No `.stale-*` litter left behind by the repair.
        Assert.Empty(Directory.GetDirectories(_root, "collide-key.stale-*"));
    }

    [Theory]
    [InlineData(false)]   // dangling entry
    [InlineData(true)]    // empty directory
    public void IsIntact_RejectsAStageThatCannotServeItsPackages(bool empty)
    {
        var stage = Path.Combine(_root, "intact-" + (empty ? "empty" : "dangling"));
        Directory.CreateDirectory(stage);
        if (!empty)
        {
            File.WriteAllText(Path.Combine(stage, "Good.app"), "real");
            File.CreateSymbolicLink(Path.Combine(stage, "Bad.app"), Path.Combine(_root, "gone.app"));
        }

        Assert.False(AlRunner.Infrastructure.PkgDedupStaging.IsIntact(stage));
    }

    [Fact]
    public void IsIntact_AcceptsAStageWhoseEntriesAllResolve()
    {
        var stage = Path.Combine(_root, "intact-good");
        Directory.CreateDirectory(stage);
        var target = Path.Combine(_root, "target.app");
        File.WriteAllText(target, "real");
        File.WriteAllText(Path.Combine(stage, "Plain.app"), "real");
        File.CreateSymbolicLink(Path.Combine(stage, "Linked.app"), target);

        Assert.True(AlRunner.Infrastructure.PkgDedupStaging.IsIntact(stage));
        Assert.False(AlRunner.Infrastructure.PkgDedupStaging.IsIntact(Path.Combine(_root, "never-created")));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two package-cache dirs holding a cross-dir (AppId, Version) duplicate — the same
    /// forcing condition BcCompilerPkgDedupRelativePathTests uses to make the dedup branch
    /// actually stage rather than return the inputs untouched. Unique ids per call so each
    /// test gets its own content-addressed key.
    /// </summary>
    private List<string> TwoDirsForcingTheStagingBranch()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var a = Path.Combine(_root, tag, "app", ".alpackages");
        var b = Path.Combine(_root, tag, "unit-tests", ".alpackages");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        var appPkgId = $"aaaaaaaa-1111-0000-0000-{tag}0000";
        var sharedId = $"bbbbbbbb-2222-0000-0000-{tag}0000";
        WriteApp(a, "AppPkg.app", appPkgId, "App Package", "Contoso", "1.0.0.0");
        WriteApp(a, "Shared.app", sharedId, "Library Assert", "Microsoft", "28.1.0.0");
        WriteApp(b, "Shared.app", sharedId, "Library Assert", "Microsoft", "28.1.0.0");

        return new List<string> { a, b };
    }

    /// <summary>
    /// The ONLY reliable way to assert a staged entry is broken. MEASURED on .NET 8 / Linux
    /// against a symlink whose target was deleted: File.Exists, FileInfo.Exists and
    /// FileInfo.ResolveLinkTarget(returnFinalTarget: true) ALL report it as present — the link
    /// is itself a directory entry that exists — and only opening it raises
    /// FileNotFoundException. An assertion written as `Assert.False(File.Exists(x))` can
    /// therefore never fail, and would leave this test passing against the unfixed code.
    /// </summary>
    private static void AssertDoesNotOpen(string path, string because)
    {
        try
        {
            using var probe = File.OpenRead(path);
            Assert.Fail(because);
        }
        catch (IOException) { /* expected: the link resolves to nothing */ }
    }

    private static void AssertOpensAsNavxPackage(string path)
    {
        // Opens, so it fails on a dangling entry — which is what BC's native package reader
        // does, and what it reports as AL1023 against the whole compilation.
        using var fs = File.OpenRead(path);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        using var payload = new MemoryStream(bytes, 8, bytes.Length - 8);
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        Assert.Contains(zip.Entries,
            e => e.FullName.Equals("NavxManifest.xml", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> InvokeDeduplicateAppPackageDirs(List<string> packageDirs, Guid? excludeAppId)
    {
        var method = typeof(BcCompiler)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "DeduplicateAppPackageDirs" && m.GetParameters().Length == 2)
            ?? throw new InvalidOperationException(
                "BcCompiler.DeduplicateAppPackageDirs(dirs, excludeAppId) not found by reflection — signature may have changed.");
        return (List<string>)method.Invoke(null, new object?[] { packageDirs, excludeAppId })!;
    }

    private static void WriteApp(string dir, string fileName,
        string appId, string name, string publisher, string version)
        => File.WriteAllBytes(Path.Combine(dir, fileName), MakeMinimalApp(appId, name, publisher, version));

    private static byte[] MakeMinimalApp(string appId, string name, string publisher, string version)
    {
        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/navx/2015/manifest">
              <App Id="{appId}" Name="{name}" Publisher="{publisher}" Version="{version}"/>
            </Package>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var es = zip.CreateEntry("NavxManifest.xml").Open())
                es.Write(Encoding.UTF8.GetBytes(xml));
            // The dedup filter drops any .app without one before staging ever runs.
            using (var ss = zip.CreateEntry("SymbolReference.json").Open())
                ss.Write(Encoding.UTF8.GetBytes("{}"));
        }
        var zipBytes = ms.ToArray();

        var result = new byte[8 + zipBytes.Length];
        result[0] = (byte)'N'; result[1] = (byte)'A'; result[2] = (byte)'V'; result[3] = (byte)'X';
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), (uint)8);
        zipBytes.CopyTo(result, 8);
        return result;
    }
}
