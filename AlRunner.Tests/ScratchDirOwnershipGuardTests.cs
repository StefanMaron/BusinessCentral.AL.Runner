using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Guards the invariant #2706 set up and #2743 finished: every scratch directory a test in
/// this project hands to the runner is OWNED, so a killed test host's leftovers can be
/// reclaimed by a later runner start instead of sitting in the temp root forever.
///
/// #2736 converted 190 sites by matching two textual shapes. About 50 did not match and were
/// left behind — including fourteen <c>--cache</c> roots, the expensive kind at roughly 273 MB
/// each, and about 30 bare <c>Path.Combine(Path.GetTempPath(), Guid)</c> directories written
/// straight into the temp root with no <c>al-runner-</c> prefix, which
/// <see cref="AlRunner.Infrastructure.ScratchDirs.SweepStale"/> cannot reach even in principle
/// (it skips any depth-0 name without a scanned prefix). Nothing noticed for three weeks,
/// because a textual conversion leaves no record of what it missed.
///
/// So the remainder is enforced here rather than described in a PR body. A new
/// <c>Path.GetTempPath()</c> in AlRunner.Tests fails this test unless its file is on the
/// allowlist below WITH the reason the site cannot be owned — and the COUNT is part of the
/// allowlist, so adding a second, ownable site to an allowlisted file still fails.
///
/// The failure mode this shape exists to avoid is the one
/// <c>.claude/rules/no-base-app-in-csharp-tests.md</c> already recorded: an allowlist written
/// as prose with no test behind it was ignored, and the violation it asked for arrived anyway.
///
/// Every allowlisted site has the same property: reserving it would change what the test is
/// testing, not just where it writes. Three kinds, and no fourth has been needed:
///
///   * A path that must NOT exist — <c>Reserve</c> creates the parent directory and writes a
///     <c>.owner</c> sidecar next to a path whose entire point is that nothing is there.
///   * A pure path-string argument to a resolver, never created on disk, so there is nothing
///     to own and a sidecar would be litter of its own.
///   * An assertion ABOUT the temp root — where the runner puts its own scratch, or that the
///     sweep walks the real root.
///
/// Comment lines are not counted: several files quote the expression while explaining that
/// they deliberately do not use it, and flagging those would train readers to ignore this
/// test.
/// </summary>
public sealed class ScratchDirOwnershipGuardTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string TestsDir => Path.Combine(RepoRoot, "AlRunner.Tests");

    private const string Expression = "Path.GetTempPath()";

    /// <summary>
    /// Source path, relative to <c>AlRunner.Tests</c> → (how many non-comment
    /// <c>Path.GetTempPath()</c> occurrences are permitted, why they cannot be owned). The
    /// count is deliberately exact in both directions: too many means a new unowned site
    /// slipped into an already-allowlisted file, too few means the entry is stale and is
    /// silently pre-approving the next one.
    /// </summary>
    private static readonly Dictionary<string, (int Count, string Why)> Allowed = new()
    {
        ["TestScratch.cs"] =
            (3, "the helper itself — Dir, FlatDir and FilePath are what everything else calls"),
        ["ScratchDirOwnershipGuardTests.cs"] =
            (1, "the literal this guard scans for, in the const it scans with"),

        // ── paths that must NOT exist ───────────────────────────────────────────────────
        ["AppLoaderManifestCacheTests.cs"] =
            (1, "a .app path that must not exist, so the loader's miss path is what runs"),
        ["AppLoaderR2rChunkCacheTests.cs"] =
            (1, "a .app path that must not exist"),
        ["RunnerFingerprintTests.cs"] =
            (1, "a .dll path that must not exist"),
        ["TestDataProvisioningTests.cs"] =
            (1, "a .bak path that must not exist, so the missing-backup diagnosis is what runs"),
        ["WatchSourceTests.cs"] =
            (1, "a bundle path that must not exist"),
        ["Win32StubsLoudFailureTests.cs"] =
            (1, "a .so path that must not exist; this file's three real directories ARE owned"),

        // ── path strings handed to a resolver, never created ────────────────────────────
        ["ArtifactsRootEnvOverrideTests.cs"] =
            (3, "AL_RUNNER_ARTIFACTS_ROOT values fed to BcArtifacts.ResolveArtifactsRoot; the "
              + "test asserts on the resolved string and never touches the filesystem"),
        ["SiblingSymbolsDirectoryTests.cs"] =
            (1, "a synthetic bundle path fed to SiblingSymbolsDirectory.ForBundle; the three "
              + "facts compare returned paths and create nothing"),

        // ── assertions about the temp root itself ───────────────────────────────────────
        ["AlRunnerPathsTests.cs"] =
            (1, "asserts an absolute path is echoed back unchanged; the temp root is just a "
              + "conveniently absolute path"),
        ["CacheRootsDisableForRunTests.cs"] =
            (1, "asserts the throwaway root the RUNNER chose is under the temp root"),
        ["NoCacheLastWinsIntegrationTests.cs"] =
            (1, "same — asserts about a root the runner chose, not one this test creates"),
        ["ScratchDirsRunnerStartupTests.cs"] =
            (1, "drives SweepStale over the real temp root, which is the behaviour under test"),
    };

    /// <summary>
    /// The allowlist key: the source path relative to <see cref="TestsDir"/>, with <c>/</c>
    /// separators on every platform. For a top-level file this is exactly the file name, which
    /// is why widening the scan below left all of the entries above unchanged — but a file in a
    /// subdirectory can no longer alias a same-named top-level entry and inherit its budget.
    /// </summary>
    private static string Key(string path) =>
        Path.GetRelativePath(TestsDir, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Every <c>.cs</c> source in the project, at ANY depth, minus build output.
    ///
    /// #3000: this enumerated <c>SearchOption.TopDirectoryOnly</c>, so the first test file
    /// added below the top level would have been unguarded — and silently, because the guard
    /// keeps reporting green for what it never looks at. <c>bin/</c> and <c>obj/</c> are
    /// skipped because the SDK writes generated sources there
    /// (<c>*.AssemblyInfo.cs</c>, <c>*.GlobalUsings.g.cs</c>); they are not test code, and
    /// whether they exist depends on whether the project has been built.
    /// </summary>
    private static IEnumerable<string> TestSources() =>
        Directory.EnumerateFiles(TestsDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !Key(p).Split('/').Any(seg => seg is "bin" or "obj"));

    /// <summary>Non-comment occurrences of the expression, per source path.</summary>
    private static Dictionary<string, int> Occurrences()
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in TestSources())
        {
            var n = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal)) continue;

                var i = 0;
                while ((i = raw.IndexOf(Expression, i, StringComparison.Ordinal)) >= 0)
                {
                    n++;
                    i += Expression.Length;
                }
            }
            if (n > 0) found[Key(path)] = n;
        }
        return found;
    }

    /// <summary>
    /// The forward direction: a file using <c>Path.GetTempPath()</c> without being on the
    /// allowlist is a scratch path nothing owns.
    /// </summary>
    [Fact]
    public void NoTestSource_BuildsAnUnownedTempPath_ExceptTheAllowlisted()
    {
        var offenders = Occurrences().Keys.Where(f => !Allowed.ContainsKey(f)).OrderBy(f => f).ToList();

        Assert.True(offenders.Count == 0,
            $"These test sources build a path under {Expression} directly, so nothing "
            + "records an owner for it and a killed test host leaks it permanently:\n  "
            + string.Join("\n  ", offenders)
            + "\n\nUse TestScratch.Dir / TestScratch.FlatDir for a directory, or "
            + "TestScratch.FilePath for a scratch file (ScratchDirs owns directories, so a "
            + "reserved FILE path writes a marker that deletes nothing). If the path genuinely "
            + "cannot be owned — it must not exist, or it is never created at all — add the "
            + "file here WITH its count and the reason. See issue #2743.");
    }

    /// <summary>
    /// The count direction: an allowlisted file may not quietly grow a new site behind its
    /// entry, and an entry whose count has dropped is stale — a stale entry pre-approves the
    /// next unowned site to land in that file.
    /// </summary>
    [Fact]
    public void EveryAllowlistEntry_MatchesItsRecordedCount_SoTheListCannotGoStale()
    {
        var found = Occurrences();
        var wrong = new List<string>();

        foreach (var (name, (count, why)) in Allowed)
        {
            var actual = found.TryGetValue(name, out var n) ? n : 0;
            if (actual != count)
                wrong.Add($"{name}: allowlist says {count}, source has {actual} ({why})");
        }

        Assert.True(wrong.Count == 0,
            "These allowlist entries no longer match the source:\n  " + string.Join("\n  ", wrong)
            + "\n\nHigher than recorded means a NEW unowned temp path was added to an "
            + "already-allowlisted file — route it through TestScratch. Lower means the entry is "
            + "stale: correct the count, or delete the entry, so it stops pre-approving the next "
            + "site that lands in that file.");
    }

    /// <summary>
    /// The helper has to actually reserve, or every one of the ~240 converted call sites is
    /// back to an unowned <c>Path.Combine</c> while all the source-scanning facts above stay
    /// green. So: each shape writes the <c>.owner</c> sidecar beside the path it returns, and
    /// this process is recorded as the owner.
    ///
    /// <see cref="TestScratch.FilePath"/> differs from the other two on purpose — it returns a
    /// FILE, and ScratchDirs cleans up directories, so the thing that must be owned is the
    /// file's parent. Asserting that distinction here is what keeps someone from "simplifying"
    /// FilePath into a Reserve of the file path, which would write a marker that deletes
    /// nothing.
    /// </summary>
    [Fact]
    public void EveryTestScratchShape_ReallyRecordsAnOwner()
    {
        var dir = TestScratch.Dir("al-runner-scratch-guard-tests");
        var flat = TestScratch.FlatDir("al-runner-scratch-guard-flat-");
        var file = TestScratch.FilePath("al-runner-scratch-guard-file", "payload.json");
        var parent = Path.GetDirectoryName(file)!;

        try
        {
            foreach (var owned in new[] { dir, flat, parent })
            {
                Assert.True(File.Exists(ScratchDirs.MarkerPathFor(owned)),
                    $"no .owner sidecar beside {owned} — a killed test host would leak it");
                Assert.True(ScratchDirs.TryReadOwner(ScratchDirs.MarkerPathFor(owned), out var pid, out _),
                    $"the sidecar beside {owned} does not parse");
                Assert.Equal(Environment.ProcessId, pid);
            }

            // FilePath's directory is created (the caller is about to write into it) while
            // Dir/FlatDir deliberately leave the leaf uncreated — several call sites observe
            // whether the RUNNER created it.
            Assert.True(Directory.Exists(parent), "TestScratch.FilePath must create the containing directory");
            Assert.False(Directory.Exists(dir), "TestScratch.Dir must not create the leaf");
            Assert.False(Directory.Exists(flat), "TestScratch.FlatDir must not create the leaf");

            // Negative: the sidecar names the directory, never the file. A marker written for
            // the file path itself would name something ScratchDirs' cleanup never deletes.
            Assert.False(File.Exists(ScratchDirs.MarkerPathFor(file)),
                "FilePath must own the containing DIRECTORY, not the file path");
        }
        finally
        {
            ScratchDirs.Release(dir);
            ScratchDirs.Release(flat);
            ScratchDirs.Release(parent);
        }
    }
}
