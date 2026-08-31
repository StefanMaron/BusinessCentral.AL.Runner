// BcVersionDefaultDocumentationTests — proves the three false claims fixed for issue
// #2209 against MEASURED behaviour, not just phrase presence.
//
// CliDocumentationTests's existing gate (Help_DocumentsEveryRecognizedFlag /
// Guide_CoversEveryBehaviorChangingFlag) only checks that a flag is MENTIONED
// somewhere in --help / --guide. That is why three stale claims survived it undetected:
//
//   1. --help claimed --bc-version's default is "the latest version present in
//      ~/.local/share/al-runner/artifacts" unconditionally. For a single-build install
//      (this repo's own dev/CI build — no variants/ dir shipped), the real default is
//      the exact BC build the engine was compiled against, NOT the highest cached
//      version (cb7d43c1 / #2036 changed this; --help was never updated).
//   2. --guide claimed "the runner does not currently print its selection". It prints
//      it on every run, on stderr, twice.
//   3. --guide's "usual real-world shape" example over-prescribed --package-cache for
//      a two-bundle (app + separate test app) invocation that runs correctly with none.
//
// Each test below spawns the REAL runner binary (the same one --help/--guide describe)
// and asserts the documented claim against what that binary actually does — so a future
// regression in either direction (docs drift from behaviour, or behaviour drifts from
// docs) fails loud here instead of shipping silently a fourth time.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class BcVersionDefaultDocumentationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>A dependency-free bundle (no [Test] procedures, one table) — fastest
    /// possible real compile+run round trip for a test that only cares about the
    /// version-selection preamble, not test execution itself.</summary>
    private static readonly string MinimalBundle =
        Path.Combine(RepoRoot, "tests", "runner-extras", "esm-xapp-table");

    private static (int ExitCode, string StdOut, string StdErr) Run(string? isolatedHome, params string[] args)
    {
        var sb = new StringBuilder(TestBuildConfig.RunArgs(Path.Combine(RepoRoot, "AlRunner")));
        foreach (var a in args) sb.Append(' ').Append(a);

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = sb.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        if (isolatedHome != null) psi.Environment["HOME"] = isolatedHome;

        var outSb = new StringBuilder();
        var errSb = new StringBuilder();
        using var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outSb) outSb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errSb) errSb.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(120_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"al-runner did not exit within 120s for args: {string.Join(' ', args)}");
        }
        proc.WaitForExit();
        lock (outSb) lock (errSb) return (proc.ExitCode, outSb.ToString(), errSb.ToString());
    }

    /// <summary>
    /// Builds a hermetic $HOME whose ONLY BC artifact directories are (a) a symlink to
    /// this machine's real, already-provisioned copy of <paramref name="engineVersion"/>
    /// (so the completeness gate — ProvisioningCheck.Check — passes without touching the
    /// network) and (b) an empty directory named as a version strictly HIGHER than it.
    /// The historical (false, pre-#2209) claim was "default = highest cached version" —
    /// (b) exists purely so that claim, if it were still true, would make the run select
    /// the empty/incomplete directory and fail loud instead of completing.
    /// </summary>
    private static string BuildIsolatedHomeWithEngineVersionAndAFakeHigherOne(Version engineVersion)
    {
        var realHome = TestArtifacts.HomeDir()
            ?? throw new InvalidOperationException("Cannot determine this machine's HOME.");
        var realEngineDir = Path.Combine(TestArtifacts.StandardCacheDir(realHome), engineVersion.ToString());
        TestArtifacts.SkipIfDirectoryMissing(realEngineDir, $"BC {engineVersion} artifacts");

        var isolatedHome = Path.Combine(Path.GetTempPath(), "al-runner-bcversion-default-doc", Guid.NewGuid().ToString("N"));
        var isolatedArtifacts = TestArtifacts.StandardCacheDir(isolatedHome);
        Directory.CreateDirectory(isolatedArtifacts);
        Directory.CreateSymbolicLink(Path.Combine(isolatedArtifacts, engineVersion.ToString()), realEngineDir);

        var fakeHigher = new Version(engineVersion.Major + 71, 9, 99999, 99999); // never a real BC version
        Directory.CreateDirectory(Path.Combine(isolatedArtifacts, fakeHigher.ToString()));
        return isolatedHome;
    }

    /// <summary>
    /// Issue #2209 claim 1, MEASURED: a single-build install (this repo's own dev/CI
    /// build — no variants/ shipped alongside AlRunner/bin/) defaults --bc-version to the
    /// exact BC build the engine was compiled against, even when a strictly newer version
    /// is also present in the cache. The pre-fix --help claimed the opposite ("Default:
    /// the latest version present in ~/.local/share/al-runner/artifacts").
    /// </summary>
    [SkippableFact]
    public void BcVersionDefault_SelectsEnginesOwnBuild_NotHighestCachedVersion()
    {
        var engineVersion = BcArtifacts.EngineBuiltVersion();
        TestArtifacts.SkipIf(engineVersion == null,
            "no baked-in BcEngineVersion on this build — nothing to measure a default against.");
        TestArtifacts.SkipIf(EngineVariants.Discover(AppContext.BaseDirectory).Count > 0,
            "this install ships engine variants — a different (also-documented) default branch applies.");

        var isolatedHome = BuildIsolatedHomeWithEngineVersionAndAFakeHigherOne(engineVersion!);
        try
        {
            // Issue #2239: the "[bc] no --bc-version given — selecting BC ..., the exact
            // build ..." reasoning line this test also asserts on moved behind --verbose
            // (the outcome is already named, unconditionally, by "[bc] selected BC ...").
            var (exit, _, stderr) = Run(isolatedHome, "--no-auto-provision", "--verbose", $"\"{MinimalBundle}\"");

            Assert.True(exit == 0, $"expected a clean run against the engine's own (real, symlinked) " +
                $"artifact; a nonzero exit means something else got selected instead. exit={exit}\n{stderr}");

            var m = Regex.Match(stderr, @"\[bc\] selected BC (\S+) \(");
            Assert.True(m.Success, $"expected a '[bc] selected BC <version> (<path>)' line. stderr:\n{stderr}");
            Assert.Equal(engineVersion!.ToString(), m.Groups[1].Value);

            Assert.Contains(
                $"[bc] no --bc-version given — selecting BC {engineVersion}, the exact build this binary " +
                "was compiled against.", stderr, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(isolatedHome, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Issue #2209 claim 1's documentation half: --help's --bc-version description must
    /// not repeat the unconditional "Default: the latest version present in ..." claim
    /// that <see cref="BcVersionDefault_SelectsEnginesOwnBuild_NotHighestCachedVersion"/>
    /// just measured to be false for this install shape, and it must instead name the
    /// behaviour that test actually measured.
    /// </summary>
    [Fact]
    public void Help_BcVersionDefaultText_DoesNotClaimUnconditionalLatestCached()
    {
        var (exit, help, _) = Run(null, "--help");
        Assert.True(exit == 0, $"--help must exit 0. exit={exit}");

        Assert.DoesNotContain("Default: the latest version present in", help, StringComparison.Ordinal);
        // Help text word-wraps at ~78 chars, so "compiled" and "against" can legitimately
        // land on adjacent lines — match across the line break rather than requiring one
        // literal substring.
        Assert.Matches(new Regex(@"compiled\s+against"), help);
        Assert.Contains("multi-variant install", help, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #2209 claim 2, MEASURED: the runner prints its BC version selection on
    /// every run (on stderr), and --guide must not claim otherwise. Checks the CLAIM
    /// against a REAL run's output, not just that the guide contains some phrase.
    /// </summary>
    [Fact]
    public void Guide_VersionSelectionClaim_MatchesRealRunOutput()
    {
        var (runExit, _, stderr) = Run(null, $"\"{MinimalBundle}\"");
        Assert.True(runExit == 0, $"expected a clean run of the minimal bundle. exit={runExit}\n{stderr}");
        Assert.Contains("[bc] selected BC ", stderr, StringComparison.Ordinal);

        var (guideExit, guide, _) = Run(null, "--guide");
        Assert.True(guideExit == 0, $"--guide must exit 0. exit={guideExit}");

        // The stale, now-false claim must be gone...
        Assert.DoesNotContain("does not currently print its selection", guide, StringComparison.Ordinal);
        // ...and the guide must name the EXACT prefix a real run just produced above, not
        // a paraphrase that could drift from the real message text unnoticed.
        Assert.Contains("[bc] selected BC ", guide, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #2209 claim 3, MEASURED: the guide's "usual real-world shape" (an app run
    /// together with its separate test app) must not prescribe --package-cache when the
    /// two bundles' own dependency resolution (the bundle's own .alpackages/ / the
    /// layered pre-pass feeding bundle 1's output to bundle 2) already covers it. Proven
    /// against the real two-bundle cross-app reproducer used elsewhere in this repo for
    /// exactly this shape (app + dependent test/consumer app).
    /// </summary>
    [Fact]
    public void Guide_UsualShapeExample_ActuallyRunsWithoutPackageCache()
    {
        var depBundle = Path.Combine(RepoRoot, "tests", "runner-extras", "xasm-event-dispatch-dep");
        var mainBundle = Path.Combine(RepoRoot, "tests", "runner-extras", "xasm-event-dispatch-main");

        var (exit, _, stderr) = Run(null, $"\"{depBundle}\"", $"\"{mainBundle}\"");
        Assert.True(exit == 0,
            $"the guide's claim that this shape needs no --package-cache must hold for a real two-bundle " +
            $"(app + dependent app) run. exit={exit}\n{stderr}");

        var (guideExit, guide, _) = Run(null, "--guide");
        Assert.True(guideExit == 0, $"--guide must exit 0. exit={guideExit}");

        var shapeIdx = guide.IndexOf("usual real-world shape", StringComparison.Ordinal);
        Assert.True(shapeIdx >= 0, "guide must still describe the app+test-app shape.");
        var nextCommandMatch = Regex.Match(guide[shapeIdx..], @"al-runner[^\r\n]*");
        Assert.True(nextCommandMatch.Success, "expected an al-runner command line following the shape description.");
        Assert.DoesNotContain("--package-cache", nextCommandMatch.Value, StringComparison.Ordinal);
    }
}
