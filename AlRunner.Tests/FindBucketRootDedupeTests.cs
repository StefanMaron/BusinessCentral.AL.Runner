// FindBucketRootDedupeTests — issue #1824.
//
// Program.cs and WatchSource.cs each had their OWN copy of the bucket-root walk-up
// (climb parent directories until an app.json is found). They started byte-identical
// (both introduced/kept in sync across #1822), but nothing enforced that: a future edit
// to one copy with the other left alone is exactly how `--watch` would start disagreeing
// with a normal run about which directory is a bundle — see the issue body's BcFloorGate
// reference for why landing on the wrong root is a silent correctness bug, not a cosmetic
// one.
//
// The fix promotes WatchSource.FindBucketRoot (already internal-testable per #1822 — see
// WatchSourceTests.cs's header) to the single shared implementation; Program.cs's local
// function now delegates to it instead of keeping its own copy.
//
// Two things this file proves, neither provable by a path-string/behavioral test alone:
//
//   1. WatchSource.FindBucketRoot's own walk-up semantics are pinned directly (positive:
//      climbs multiple levels to the NEAREST app.json, not the bundle path itself and not
//      a farther ancestor; negative: returns null when no app.json exists anywhere up to
//      the filesystem root). This was previously private — reachable by NEITHER
//      WatchSourceTests.cs (which only tests it indirectly via ArmSourceWatch) NOR any
//      other class, since `private` blocks access regardless of InternalsVisibleTo. So
//      before the #1824 fix, this file fails to even COMPILE against WatchSource.cs (RED
//      via CS0122 inaccessible-due-to-protection-level) — the promotion to `internal` is
//      itself part of the fix under test, not incidental.
//
//   2. A structural guard on Program.cs's OWN source text: its FindBucketRoot local
//      function must no longer contain an independent walk-up loop. A test that only
//      checked Program.cs's *output* (e.g. spawning the runner against a nested bundle)
//      would pass equally well whether Program.cs calls WatchSource.FindBucketRoot or
//      still carries its own byte-identical copy — the two are behaviorally
//      indistinguishable from outside, which is exactly the duplication this issue exists
//      to remove. Reading the source is the only way to prove there is now ONE
//      implementation, not two that happen to still agree.
using Xunit;

namespace AlRunner.Tests;

public sealed class FindBucketRootDedupeTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "al-runner-findbucketroot-dedupe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FindBucketRoot_ClimbsMultipleLevels_ToTheNearestAppJson()
    {
        // root/app.json
        // root/sub/deeper/leaf   <- bundlePath passed in
        // Must land on `root`, not `root/sub` (no app.json there) and not `root/sub/deeper`.
        var root = NewTempDir();
        File.WriteAllText(Path.Combine(root, "app.json"), "{}");
        var leaf = Path.Combine(root, "sub", "deeper", "leaf");
        Directory.CreateDirectory(leaf);

        var found = AlRunner.WatchSource.FindBucketRoot(leaf);

        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(found!));
    }

    [Fact]
    public void FindBucketRoot_PrefersTheNearestAppJson_NotAFartherAncestorsToo()
    {
        // root/app.json                 <- a FARTHER app.json that must NOT win
        // root/inner/app.json           <- the NEAREST one; this must win
        // root/inner/leaf               <- bundlePath passed in
        var root = NewTempDir();
        File.WriteAllText(Path.Combine(root, "app.json"), "{}");
        var inner = Path.Combine(root, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "app.json"), "{}");
        var leaf = Path.Combine(inner, "leaf");
        Directory.CreateDirectory(leaf);

        var found = AlRunner.WatchSource.FindBucketRoot(leaf);

        Assert.Equal(Path.GetFullPath(inner), Path.GetFullPath(found!));
    }

    [Fact]
    public void FindBucketRoot_NoAppJsonAnywhereUpTheTree_ReturnsNull()
    {
        var leaf = NewTempDir();
        // NewTempDir's own ancestor chain (Path.GetTempPath()'s tree) genuinely has no
        // app.json in this test environment, so this exercises the real "climbed all the
        // way to the filesystem root without finding one" path, not a mocked stand-in.
        var found = AlRunner.WatchSource.FindBucketRoot(leaf);

        Assert.Null(found);
    }

    [Fact]
    public void FindBucketRoot_BundlePathIsAFileNotADirectory_StartsFromItsContainingDirectory()
    {
        // A bundle path that points at a FILE (e.g. a single .al file passed on the CLI)
        // must start climbing from its containing directory, not fail because the path
        // itself isn't a directory.
        var root = NewTempDir();
        File.WriteAllText(Path.Combine(root, "app.json"), "{}");
        var filePath = Path.Combine(root, "Some.Table.al");
        File.WriteAllText(filePath, "table 60000 Some { }");

        var found = AlRunner.WatchSource.FindBucketRoot(filePath);

        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(found!));
    }

    [Fact]
    public void ProgramCs_FindBucketRoot_DelegatesToWatchSource_HasNoIndependentWalkUpLoop()
    {
        // Structural guard: read Program.cs's own source and assert its FindBucketRoot
        // local function body is a one-line delegation, not a second copy of the walk-up
        // loop. A behavioral test alone (spawn the runner, check which root it picks)
        // cannot distinguish "calls the shared implementation" from "still carries its
        // own byte-identical copy" — both produce the same observable output. Only
        // reading the source proves there is genuinely ONE implementation now.
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var programCsPath = Path.Combine(repoRoot, "AlRunner", "Program.cs");
        Assert.True(File.Exists(programCsPath), $"expected to find {programCsPath}");
        var source = File.ReadAllText(programCsPath);

        var marker = "static string? FindBucketRoot(string bundlePath)";
        var idx = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(idx >= 0, "expected a FindBucketRoot local function declaration in Program.cs");

        // Grab a window of source starting at the declaration, long enough to contain
        // either the old multi-line walk-up loop or the new one-line delegation, but
        // short enough that it can't accidentally reach into an unrelated later method.
        var window = source.Substring(idx, Math.Min(400, source.Length - idx));

        Assert.Contains("WatchSource.FindBucketRoot(bundlePath)", window);
        // The old copy's tell-tale walk-up internals must be gone from THIS function —
        // if they're still here, Program.cs regressed to carrying its own copy again.
        Assert.DoesNotContain("Directory.Exists(bundlePath)", window);
        Assert.DoesNotContain("Path.GetDirectoryName(cur)", window);
    }
}
