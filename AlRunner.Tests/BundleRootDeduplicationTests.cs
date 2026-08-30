// BundleRootDeduplicationTests — RED->GREEN guard for issue #2136.
//
// The bug: the same bundle directory passed twice on one command line ran twice, so a
// 1-test bundle reported 2 tests. Nothing crashed (that was #1692, fixed), but the test
// count silently doubled — and the test count is what CI gates on and what
// tests/expectations/count-baseline/ pins.
//
// The behaviour pinned here: two positional arguments that name the SAME DIRECTORY ON
// DISK run once. "Same directory" is decided on the resolved real path — absolute,
// with '.'/'..' collapsed, trailing separators trimmed, and symlinks followed — not on
// the argument string, so `x`, `./x`, `/abs/x`, `x/` and a symlink to `x` all collapse
// onto one another. The first spelling wins and argument order is preserved.
//
// Ghost-test trap avoided in both directions:
//   * a no-op "fix" (or a raw-string Distinct(), which fixes only the identical-spelling
//     case) fails RelativeAndAbsolute_*, TrailingSeparator_*, DotSegment_* and
//     Symlink_*, because each asserts a concrete kept/dropped list, not merely
//     "did not crash";
//   * an over-eager fix that collapses distinct directories fails
//     DistinctDirectories_AreBothKept and SameAppIdentityInTwoDirectories_AreBothKept —
//     the latter pins that bundle IDENTITY is deliberately NOT the dedup key. Two
//     different directories declaring the same app id already have their own loud,
//     deliberate handling (the #1683 "AppId … already loaded earlier in this process"
//     module reuse); collapsing them here would silently discard an argument the user
//     really did type twice on purpose.
using System.Diagnostics;
using System.Text;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BundleRootDeduplicationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public BundleRootDeduplicationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-bundle-dedup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    // ── unit: the resolved-path dedup itself ─────────────────────────────────────

    [Fact]
    public void IdenticalSpelling_RunsOnce_AndNamesTheDroppedArgument()
    {
        var a = MakeDir("alpha");

        var r = BundleRootDeduplication.Deduplicate(new[] { a, a });

        Assert.Equal(new[] { a }, r.Roots);
        var dropped = Assert.Single(r.Dropped);
        Assert.Equal(a, dropped.Argument);
        Assert.Equal(a, dropped.KeptArgument);
        Assert.Equal(a, dropped.ResolvedPath);
    }

    [Fact]
    public void RelativeAndAbsolute_CollapseToOne_KeepingTheFirstSpelling()
    {
        var a = MakeDir("alpha");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, a);

        var r = BundleRootDeduplication.Deduplicate(new[] { relative, a });

        Assert.Equal(new[] { relative }, r.Roots);
        var dropped = Assert.Single(r.Dropped);
        Assert.Equal(a, dropped.Argument);
        Assert.Equal(relative, dropped.KeptArgument);
        Assert.Equal(a, dropped.ResolvedPath);
    }

    [Fact]
    public void TrailingSeparator_CollapsesOntoTheSamePath()
    {
        var a = MakeDir("alpha");

        var r = BundleRootDeduplication.Deduplicate(new[] { a, a + Path.DirectorySeparatorChar });

        Assert.Equal(new[] { a }, r.Roots);
        Assert.Single(r.Dropped);
    }

    [Fact]
    public void DotSegment_CollapsesOntoTheSamePath()
    {
        var a = MakeDir("alpha");
        var viaDots = Path.Combine(_root, "alpha", "..", "alpha");

        var r = BundleRootDeduplication.Deduplicate(new[] { a, viaDots });

        Assert.Equal(new[] { a }, r.Roots);
        Assert.Equal(a, Assert.Single(r.Dropped).ResolvedPath);
    }

    [SkippableFact]
    public void Symlink_AndItsTarget_CollapseToOne()
    {
        var a = MakeDir("alpha");
        var link = Path.Combine(_root, "alpha-link");
        Skip.IfNot(TryCreateSymlink(a, link), "the filesystem does not allow creating symlinks here");

        var r = BundleRootDeduplication.Deduplicate(new[] { a, link });

        Assert.Equal(new[] { a }, r.Roots);
        Assert.Equal(link, Assert.Single(r.Dropped).Argument);
    }

    [SkippableFact]
    public void Symlink_InAnIntermediateComponent_CollapsesToo()
    {
        var real = MakeDir(Path.Combine("real-parent", "alpha"));
        var linkParent = Path.Combine(_root, "link-parent");
        Skip.IfNot(TryCreateSymlink(Path.Combine(_root, "real-parent"), linkParent),
            "the filesystem does not allow creating symlinks here");
        var viaLink = Path.Combine(linkParent, "alpha");

        var r = BundleRootDeduplication.Deduplicate(new[] { real, viaLink });

        Assert.Equal(new[] { real }, r.Roots);
        Assert.Equal(real, Assert.Single(r.Dropped).ResolvedPath);
    }

    [SkippableFact]
    public void RelativeSymlink_ResolvesAgainstTheLinkDirectory_NotTheWorkingDirectory()
    {
        var a = MakeDir("alpha");
        var link = Path.Combine(_root, "alpha-rel-link");
        Skip.IfNot(TryCreateSymlink("alpha", link),   // stored relative to _root
            "the filesystem does not allow creating symlinks here");

        Assert.Equal(a, BundleRootDeduplication.Canonicalize(link));
    }

    // ── negative direction: genuinely different bundles must still both run ──────

    [Fact]
    public void DistinctDirectories_AreBothKept()
    {
        var a = MakeDir("alpha");
        var b = MakeDir("beta");

        var r = BundleRootDeduplication.Deduplicate(new[] { a, b });

        Assert.Equal(new[] { a, b }, r.Roots);
        Assert.Empty(r.Dropped);
    }

    [Fact]
    public void SameAppIdentityInTwoDirectories_AreBothKept()
    {
        // Identity is deliberately NOT the dedup key — see the header comment.
        const string manifest = """
        { "id": "a1b2c3d4-2136-4a1b-9c3d-000000000001", "name": "Dup 2136",
          "publisher": "Repro2136", "version": "1.0.0.0", "dependencies": [],
          "platform": "1.0.0.0", "application": "1.0.0.0",
          "idRanges": [ { "from": 62430, "to": 62439 } ], "runtime": "14.0" }
        """;
        var a = MakeDir("copy-a");
        var b = MakeDir("copy-b");
        File.WriteAllText(Path.Combine(a, "app.json"), manifest);
        File.WriteAllText(Path.Combine(b, "app.json"), manifest);

        var r = BundleRootDeduplication.Deduplicate(new[] { a, b });

        Assert.Equal(new[] { a, b }, r.Roots);
        Assert.Empty(r.Dropped);
    }

    [Fact]
    public void ThreeWayDuplicate_KeepsOne_AndReportsBothDrops()
    {
        var a = MakeDir("alpha");
        var b = MakeDir("beta");

        var r = BundleRootDeduplication.Deduplicate(
            new[] { a, b, a + Path.DirectorySeparatorChar, Path.Combine(_root, "alpha", ".") });

        Assert.Equal(new[] { a, b }, r.Roots);
        Assert.Equal(2, r.Dropped.Count);
        Assert.All(r.Dropped, d => Assert.Equal(a, d.KeptArgument));
    }

    [Fact]
    public void NoDuplicates_ProduceNoNotice()
    {
        var r = BundleRootDeduplication.Deduplicate(new[] { MakeDir("alpha"), MakeDir("beta") });
        Assert.Null(BundleRootDeduplication.DescribeDropped(r.Dropped));
    }

    [Fact]
    public void Notice_NamesBothSpellingsAndTheResolvedPath()
    {
        var a = MakeDir("alpha");
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, a);

        var r = BundleRootDeduplication.Deduplicate(new[] { relative, a });
        var notice = BundleRootDeduplication.DescribeDropped(r.Dropped);

        Assert.NotNull(notice);
        Assert.Contains("duplicate bundle argument", notice);
        Assert.Contains(relative, notice);
        Assert.Contains(a, notice);
    }

    [Fact]
    public void Canonicalize_DoesNotThrow_OnAPathThatDoesNotExist()
    {
        var missing = Path.Combine(_root, "no", "such", "dir");
        Assert.Equal(missing, BundleRootDeduplication.Canonicalize(missing));
    }

    [Fact]
    public void EmptyAndWhitespaceArguments_AreLeftAlone()
    {
        var r = BundleRootDeduplication.Deduplicate(new[] { "", " " });
        Assert.Equal(new[] { "", " " }, r.Roots);
        Assert.Empty(r.Dropped);
    }

    // ── CLI: the reported test count is what the issue is actually about ─────────

    [SkippableFact]
    public void Cli_SameBundleTwice_RunsOnceAndReportsOneTest()
    {
        TestArtifacts.SkipIfMissing();
        var fixture = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");

        var single = RunRunner(fixture);
        Assert.True(single.exit == 0, single.output);
        Assert.True(single.output.Contains("Tests:         1 total"), single.output);

        var doubled = RunRunner(fixture, fixture);

        Assert.True(doubled.exit == 0, doubled.output);
        // The count is the whole point of #2136: two arguments must not double it.
        Assert.True(doubled.output.Contains("Tests:         1 total"), doubled.output);
        Assert.True(doubled.output.Contains("Buckets:       1 total"), doubled.output);
        Assert.True(doubled.output.Contains("duplicate bundle argument"), doubled.output);
    }

    [SkippableFact]
    public void Cli_TwoDistinctBundles_StillRunTwice()
    {
        TestArtifacts.SkipIfMissing();
        var fixture = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "RecordTriggerXRec");
        var copy = MakeDir("cli-copy");
        foreach (var f in Directory.GetFiles(fixture))
            File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));

        var r = RunRunner(fixture, copy);

        Assert.True(r.exit == 0, r.output);
        Assert.True(r.output.Contains("Tests:         2 total"), r.output);
        Assert.True(!r.output.Contains("duplicate bundle argument"), r.output);
    }

    private static bool TryCreateSymlink(string target, string link)
    {
        try { Directory.CreateSymbolicLink(link, target); return Directory.Exists(link); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (PlatformNotSupportedException) { return false; }
    }

    private static (string output, int exit) RunRunner(params string[] bundles)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        foreach (var b in bundles) args.Append(" \"").Append(b).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }
}
