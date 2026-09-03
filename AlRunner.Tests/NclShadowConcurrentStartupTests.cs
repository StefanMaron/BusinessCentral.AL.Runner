// NclShadowConcurrentStartupTests — #2489: several `al-runner` processes racing to build
// the SAME `ncl-shadow` cache root observed four outcomes: prune deleting files out of a
// sibling's in-flight `.building.*` temp dir, a resulting shadow dir published complete
// enough to pass the old rename-into-place check but missing its entry DLL forever after
// (sticky — nothing revisited it once published), the `MoveDirectory ... Access is
// denied` crash that surfaced only the rarest of those, and every re-exec'd child
// redundantly re-copying Microsoft.Dynamics.Nav.Ncl.dll into a dir siblings are reading
// from. This file pins the three mechanism fixes directly, deterministically — no
// subprocess race needed since each fix is a pure function over on-disk shape.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclShadowConcurrentStartupTests
{
    private static string NewTempDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ncl-shadow-race-{label}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string MarkerFileName = ".al-runner-shadow-source";
    private const string EntryDllName = "al-runner.dll";
    private const string NclFileName = "Microsoft.Dynamics.Nav.Ncl.dll";

    private static void WriteCompleteShadowDir(string dir, string origFull, byte[]? dllBytes = null)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MarkerFileName), origFull);
        File.WriteAllBytes(Path.Combine(dir, EntryDllName), dllBytes ?? new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(dir, NclFileName), new byte[] { 4, 5, 6 });
        File.WriteAllText(Path.Combine(dir, "al-runner.deps.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "al-runner.runtimeconfig.json"), "{}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PruneStaleShadowDirs — must never touch a sibling's *.building.* temp dir
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive + regression pin: even when the shadow root holds far more than
    /// `keepNewest` entries, any directory whose name contains ".building." is excluded
    /// from BOTH the candidate set and the keepNewest count — it must survive untouched,
    /// content and all, no matter how old its LastWriteTimeUtc looks. Before this fix a
    /// `.building.*` dir was ordinary prune fodder once the root held more than
    /// `keepNewest` total entries; #2489 measured this deleting files out of a sibling's
    /// in-flight build 44 times across 5 runs at N=10.</summary>
    [Fact]
    public void PruneStaleShadowDirs_NeverDeletesBuildingDirs_EvenWhenOldestAndRootIsFull()
    {
        var root = NewTempDir("prune-protects-building");
        try
        {
            // 6 old "published" dirs (all older than the building dir) plus 1 in-flight
            // .building dir that is, by name pattern alone, the one a naive LRU-by-time
            // prune would pick as "oldest and therefore prunable first" once the .building
            // dir's LastWriteTimeUtc is made even older via a stale touch below.
            var buildingDir = Path.Combine(root, "somekey.building." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(buildingDir);
            var sentinel = Path.Combine(buildingDir, "Microsoft.Extensions.Http.dll");
            File.WriteAllText(sentinel, "mid-copy content a sibling is still writing");
            Directory.SetLastWriteTimeUtc(buildingDir, DateTime.UtcNow.AddHours(-10));

            for (var i = 0; i < 6; i++)
            {
                var d = Path.Combine(root, $"published-{i}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "x.txt"), "x");
                Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddHours(-1 - i));
            }

            var protectedDir = Path.Combine(root, "protected-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(protectedDir);

            NclShadowRuntime.PruneStaleShadowDirs(root, protectedDir, keepNewest: 2);

            Assert.True(Directory.Exists(buildingDir), "a .building.* dir must never be pruned");
            Assert.True(File.Exists(sentinel), "content mid-copy inside a .building.* dir must survive prune");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Negative (cost-control half): a directory that does NOT carry ".building."
    /// in its name is still ordinary prune fodder once the root exceeds keepNewest — the
    /// exclusion above must be specific to the building-marker pattern, not a regression
    /// into "prune never deletes anything".</summary>
    [Fact]
    public void PruneStaleShadowDirs_StillPrunesOrdinaryStaleDirs()
    {
        var root = NewTempDir("prune-still-works");
        try
        {
            var oldest = Path.Combine(root, "published-oldest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(oldest);
            Directory.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-30));

            for (var i = 0; i < 3; i++)
            {
                var d = Path.Combine(root, $"published-{i}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(d);
                Directory.SetLastWriteTimeUtc(d, DateTime.UtcNow.AddHours(-1 - i));
            }

            var protectedDir = Path.Combine(root, "protected-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(protectedDir);

            NclShadowRuntime.PruneStaleShadowDirs(root, protectedDir, keepNewest: 2);

            Assert.False(Directory.Exists(oldest), "an ordinary (non-.building.) stale dir must still be pruned");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsShadowDirComplete
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IsShadowDirComplete_AllFilesPresentAndMarkerMatches_ReturnsTrue()
    {
        var dir = NewTempDir("complete-true");
        try
        {
            WriteCompleteShadowDir(dir, origFull: @"C:\install\any");
            Assert.True(NclShadowRuntime.IsShadowDirComplete(dir, @"C:\install\any"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Negative + regression pin for outcome 3: marker present but the entry DLL
    /// missing — exactly the shape #2489 measured as sticky output of a partial-prune
    /// race (missing, variously, al-runner.dll / al-runner.exe / al-runner.deps.json /
    /// .al-runner-shadow-source).</summary>
    [Fact]
    public void IsShadowDirComplete_MarkerPresentButEntryDllMissing_ReturnsFalse()
    {
        var dir = NewTempDir("complete-missing-dll");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), @"C:\install\any");
            File.WriteAllBytes(Path.Combine(dir, NclFileName), new byte[] { 1 });
            // al-runner.dll deliberately absent.

            Assert.False(NclShadowRuntime.IsShadowDirComplete(dir, @"C:\install\any"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Negative + regression pin: the issue's own field observation listed
    /// al-runner.deps.json among the files variously missing from an incomplete
    /// published dir. A dir with the entry DLL and Ncl.dll present but the deps
    /// manifest missing still fails `dotnet exec` (hostfxr needs it), so it must not be
    /// reported complete.</summary>
    [Fact]
    public void IsShadowDirComplete_DepsManifestMissing_ReturnsFalse()
    {
        var dir = NewTempDir("complete-missing-deps");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), @"C:\install\any");
            File.WriteAllBytes(Path.Combine(dir, EntryDllName), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, NclFileName), new byte[] { 1 });
            File.WriteAllText(Path.Combine(dir, "al-runner.runtimeconfig.json"), "{}");
            // al-runner.deps.json deliberately absent.

            Assert.False(NclShadowRuntime.IsShadowDirComplete(dir, @"C:\install\any"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsShadowDirComplete_MarkerPointsAtDifferentInstall_ReturnsFalse()
    {
        var dir = NewTempDir("complete-wrong-install");
        try
        {
            WriteCompleteShadowDir(dir, origFull: @"C:\install\OTHER");
            Assert.False(NclShadowRuntime.IsShadowDirComplete(dir, @"C:\install\any"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsShadowDirComplete_DirDoesNotExist_ReturnsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncl-shadow-race-does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.False(NclShadowRuntime.IsShadowDirComplete(dir, @"C:\install\any"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PublishShadowDir — atomic publish + self-heal (outcomes 2 and 3)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive: the ordinary, uncontended case — shadowDir does not exist yet,
    /// so tempDir is simply renamed into place.</summary>
    [Fact]
    public void PublishShadowDir_ShadowDirAbsent_MovesTempDirIntoPlace()
    {
        var root = NewTempDir("publish-clean");
        try
        {
            var origFull = @"C:\install\any";
            var tempDir = Path.Combine(root, "key.building.abc");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 9, 9, 9 });
            var shadowDir = Path.Combine(root, "key");

            NclShadowRuntime.PublishShadowDir(tempDir, shadowDir, origFull);

            Assert.False(Directory.Exists(tempDir), "temp dir must be consumed by the move");
            Assert.True(NclShadowRuntime.IsShadowDirComplete(shadowDir, origFull));
            Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(Path.Combine(shadowDir, EntryDllName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Positive + regression pin for outcome 2 (the ORIGINAL lost-race handler's
    /// job, preserved): a sibling already published a COMPLETE shadowDir first — adopt it,
    /// discard our own temp build, and do not overwrite the winner's content.</summary>
    [Fact]
    public void PublishShadowDir_SiblingAlreadyPublishedComplete_AdoptsWinnerAndDiscardsOwnBuild()
    {
        var root = NewTempDir("publish-adopt");
        try
        {
            var origFull = @"C:\install\any";
            var shadowDir = Path.Combine(root, "key");
            WriteCompleteShadowDir(shadowDir, origFull, dllBytes: new byte[] { 1, 1, 1 }); // the "winner"

            var tempDir = Path.Combine(root, "key.building.def");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 2, 2, 2 }); // our own build

            NclShadowRuntime.PublishShadowDir(tempDir, shadowDir, origFull);

            Assert.False(Directory.Exists(tempDir), "own temp build must be discarded on a lost clean race");
            Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(Path.Combine(shadowDir, EntryDllName)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Positive + the actual #2489 regression pin (outcome 3): a sibling
    /// (or a past run, before this fix) published an INCOMPLETE shadowDir — marker present,
    /// entry DLL missing. Before this fix that state was sticky forever: Directory.Move
    /// onto an existing dir always throws, and the old handler treated ANY IOException
    /// with Directory.Exists(shadowDir) true as "someone else won cleanly", discarding the
    /// good build and leaving the broken one in place. Now: self-heal — move the broken
    /// one aside, publish the good build in its place.</summary>
    [Fact]
    public void PublishShadowDir_SiblingPublishedIncomplete_SelfHealsByReplacingWithOwnCompleteBuild()
    {
        var root = NewTempDir("publish-selfheal");
        try
        {
            var origFull = @"C:\install\any";
            var shadowDir = Path.Combine(root, "key");
            Directory.CreateDirectory(shadowDir);
            File.WriteAllText(Path.Combine(shadowDir, MarkerFileName), origFull);
            File.WriteAllBytes(Path.Combine(shadowDir, NclFileName), new byte[] { 4, 5, 6 });
            // al-runner.dll deliberately missing — the exact sticky shape #2489 measured.

            var tempDir = Path.Combine(root, "key.building.ghi");
            WriteCompleteShadowDir(tempDir, origFull, dllBytes: new byte[] { 7, 7, 7 });

            NclShadowRuntime.PublishShadowDir(tempDir, shadowDir, origFull);

            Assert.True(NclShadowRuntime.IsShadowDirComplete(shadowDir, origFull),
                "the incomplete published dir must be healed into a complete one");
            Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(Path.Combine(shadowDir, EntryDllName)));
            Assert.False(Directory.Exists(tempDir));

            // No leftover ".stale." dir from the self-heal (best-effort cleanup succeeded
            // in this uncontended scenario).
            Assert.DoesNotContain(Directory.GetDirectories(root),
                d => Path.GetFileName(d).Contains(".stale.", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Negative: after self-healing, a subsequent EnsureShadowDir-style reuse
    /// check against the now-healed dir reports it reusable — proving the fix actually
    /// un-sticks the state rather than merely not-crashing once.</summary>
    [Fact]
    public void PublishShadowDir_AfterSelfHeal_ResultIsReusableOnNextCheck()
    {
        var root = NewTempDir("publish-selfheal-reusable");
        try
        {
            var origFull = @"C:\install\any";
            var shadowDir = Path.Combine(root, "key");
            Directory.CreateDirectory(shadowDir);
            File.WriteAllText(Path.Combine(shadowDir, MarkerFileName), origFull);
            // Neither al-runner.dll nor Ncl.dll present — worse than the field case, still
            // must heal.

            var tempDir = Path.Combine(root, "key.building.jkl");
            WriteCompleteShadowDir(tempDir, origFull);

            NclShadowRuntime.PublishShadowDir(tempDir, shadowDir, origFull);

            Assert.True(NclShadowRuntime.IsShadowDirComplete(shadowDir, origFull));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NclCecilRewrite.FileContentEquals — outcome 4 (redundant child re-copy)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive: identical content at both paths compares equal — the case
    /// RewriteInPlace's cache-HIT branch now uses to skip a pointless (and, under
    /// concurrent shadow-dir startups, actively racy) re-copy of Ncl.dll that a sibling
    /// or the parent EnsureShadowDir build already wrote.</summary>
    [Fact]
    public void FileContentEquals_IdenticalBytes_ReturnsTrue()
    {
        var dir = NewTempDir("content-equal");
        try
        {
            var a = Path.Combine(dir, "a.dll");
            var b = Path.Combine(dir, "b.dll");
            var bytes = new byte[50000];
            new Random(42).NextBytes(bytes);
            File.WriteAllBytes(a, bytes);
            File.WriteAllBytes(b, bytes);

            Assert.True(NclCecilRewrite.FileContentEquals(a, b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Negative: different content (even same length) compares unequal — proves
    /// the check reads bytes rather than just comparing size/timestamps, which would let a
    /// genuinely stale destination silently keep wrong content.</summary>
    [Fact]
    public void FileContentEquals_SameLengthDifferentBytes_ReturnsFalse()
    {
        var dir = NewTempDir("content-unequal");
        try
        {
            var a = Path.Combine(dir, "a.dll");
            var b = Path.Combine(dir, "b.dll");
            File.WriteAllBytes(a, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(b, new byte[] { 1, 2, 3, 5 });

            Assert.False(NclCecilRewrite.FileContentEquals(a, b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Negative: different length compares unequal without needing to read the
    /// larger file (the cheap-path assertion is implicit — this just proves correctness).</summary>
    [Fact]
    public void FileContentEquals_DifferentLength_ReturnsFalse()
    {
        var dir = NewTempDir("content-unequal-len");
        try
        {
            var a = Path.Combine(dir, "a.dll");
            var b = Path.Combine(dir, "b.dll");
            File.WriteAllBytes(a, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(b, new byte[] { 1, 2, 3, 4, 5 });

            Assert.False(NclCecilRewrite.FileContentEquals(a, b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Negative: destination missing entirely is "not equal", not an exception —
    /// RewriteInPlace's normal write path must still run for a fresh shadowDir build.</summary>
    [Fact]
    public void FileContentEquals_DestMissing_ReturnsFalse()
    {
        var dir = NewTempDir("content-dest-missing");
        try
        {
            var a = Path.Combine(dir, "a.dll");
            File.WriteAllBytes(a, new byte[] { 1, 2, 3 });
            var b = Path.Combine(dir, "does-not-exist.dll");

            Assert.False(NclCecilRewrite.FileContentEquals(a, b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Concurrency: N in-process PublishShadowDir races, deterministically ordered
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Positive: N concurrent PublishShadowDir calls against the SAME shadowDir
    /// key (mirroring N daemons started at once, all computing the same content-hash
    /// key) all succeed with no unhandled exception, and the final published dir is
    /// complete — never left half-built, never left with one process's crash. Uses real
    /// Task.Run concurrency (not a fixed interleaving) since the property under test —
    /// "every attempt converges to some ONE complete result" — must hold for whichever
    /// thread happens to win, not for one specific schedule.</summary>
    [Fact]
    public void PublishShadowDir_NConcurrentPublishersToSameKey_AllSucceedAndResultIsComplete()
    {
        var root = NewTempDir("publish-concurrent");
        try
        {
            var origFull = @"C:\install\any";
            var shadowDir = Path.Combine(root, "key");
            const int n = 10;

            var tempDirs = new string[n];
            for (var i = 0; i < n; i++)
            {
                tempDirs[i] = Path.Combine(root, $"key.building.{i:D2}-{Guid.NewGuid():N}");
                WriteCompleteShadowDir(tempDirs[i], origFull, dllBytes: BitConverter.GetBytes(i));
            }

            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            var tasks = tempDirs.Select(td => Task.Run(() =>
            {
                try { NclShadowRuntime.PublishShadowDir(td, shadowDir, origFull); }
                catch (Exception ex) { exceptions.Add(ex); }
            })).ToArray();
            var allCompleted = Task.WaitAll(tasks, TimeSpan.FromSeconds(30));

            Assert.True(allCompleted, "all N publishers must finish within the timeout");
            Assert.Empty(exceptions);
            Assert.True(NclShadowRuntime.IsShadowDirComplete(shadowDir, origFull),
                "the published dir must be complete after N concurrent publishers race to the same key");

            // Every temp dir must have been consumed (moved or deleted) — none left
            // orphaned under contention.
            foreach (var td in tempDirs)
                Assert.False(Directory.Exists(td), $"{td} must not be left behind");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
