// CountBaselineIntegrationTests — real RED→GREEN guard for #1880 (--count-baseline).
//
// The gap: --strict fails a run when a test FAILS, but nothing asserts that the
// expected NUMBER of tests actually ran. A bundle that silently stops being
// discovered still exits 0 as long as every SURVIVING test passes.
//
// These spawn the real runner (same TestBuildConfig.RunArgs idiom as
// DefineFlagIntegrationTests — direct al-runner.dll invocation, no MSBuild
// evaluation) against a tiny two-test fixture, and prove --count-baseline is an
// EXACT match, not a floor (PR #1882 review: a "growth never fails" rule lets the
// baseline go stale on a passing run, and a later real drop can then land above the
// stale number unnoticed):
//   - a baseline set ABOVE the actual count fails the run with exit 4, naming the
//     suite, the expected count and the actual count (RED — the "a bundle silently
//     stopped being discovered" scenario, reproduced deliberately);
//   - a baseline set AT the actual count does not fail (GREEN — an unchanged run);
//   - a baseline set BELOW the actual count ALSO fails the run with exit 4, naming
//     the suite, the expected count and the actual count (RED — the "the baseline
//     itself went stale" scenario: growth is normal, but an unbumped baseline must
//     not stay green);
//   - the SAME above-actual baseline that would drop the whole-bundle run is stood
///    down when --test narrows scope on purpose (mirrors the xmlport-isolation CI
//     leg, which runs the same al-language root filtered).
//   - an expected APP-GROUP count (not just tests) mismatching fires the same way.
//
// A gutted implementation (--count-baseline parsed but never compared, always
// returning "no mismatch", or one that only fails on drops) would pass GREEN tests
// here but fail every RED one below — these are not satisfiable by a no-op or by
// the pre-review floor semantics.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class CountBaselineIntegrationTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;
    private readonly string _suiteKey;
    private readonly string _baselinePath;

    public CountBaselineIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-count-baseline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _suiteKey = Path.GetFileName(_root);
        _baselinePath = Path.Combine(Path.GetTempPath(), "al-runner-count-baseline",
            "baseline-" + Guid.NewGuid().ToString("N") + ".json");
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { File.Delete(_baselinePath); } catch { }
    }

    /// <summary>
    /// A minimal AL package (no dependencies) with exactly TWO passing [Test]
    /// procedures in ONE app group — so "tests" and "appGroups" expected counts are
    /// both exercisable from one fixture (tests=2, appGroups=1).
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "b2c3d4e5-f6a7-4890-bcde-f12345678901",
          "name": "Count Baseline Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62200, "to": 62209 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "CountBaselineFixtureTests.Codeunit.al"), """
        codeunit 62200 "Count Baseline Fixture Tests"
        {
            Subtype = Test;

            [Test]
            procedure FirstAlwaysPasses()
            begin
                if 1 <> 1 then
                    Error('unreachable');
            end;

            [Test]
            procedure SecondAlwaysPasses()
            begin
                if 2 <> 2 then
                    Error('unreachable');
            end;
        }
        """);
    }

    private void WriteBaseline(string json) => File.WriteAllText(_baselinePath, json);

    private string TestsBaseline(int testsDefault) =>
        $$"""
        { "suites": { "{{_suiteKey}}": { "tests": { "default": {{testsDefault}} } } } }
        """;

    private string AppGroupsBaseline(int appGroupsDefault) =>
        $$"""
        { "suites": { "{{_suiteKey}}": { "appGroups": { "default": {{appGroupsDefault}} } } } }
        """;

    private (string output, int exit) RunRunner(params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --strict");
        args.Append($" --count-baseline \"{_baselinePath}\"");
        args.Append($" \"{_root}\"");
        foreach (var a in extraArgs) args.Append($" {a}");
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
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// RED: the fixture has 2 tests; a baseline of 3 simulates "a bundle silently
    /// stopped being discovered" (one test's worth of coverage missing). Must exit 4
    /// (not 0/1/2/3 — those already mean something else) and the message must name
    /// the suite, the expected count and the actual count.
    /// </summary>
    [SkippableFact]
    public void Drop_TestsBelowExpected_Exits4WithSuiteExpectedAndActual()
    {
        TestArtifacts.SkipIfMissing();
        WriteBaseline(TestsBaseline(testsDefault: 3));

        var (output, exit) = RunRunner();

        Assert.Equal(4, exit);
        Assert.Contains("[count-baseline] DROP", output);
        Assert.Contains($"suite '{_suiteKey}'", output);
        Assert.Contains("expected 3", output);
        Assert.Contains("actual 2", output);
        // The underlying tests themselves must NOT have failed — this exit code is
        // attributable ONLY to the count guard, not to a real test failure. Proves the
        // guard is a distinct signal, not a relabeling of the existing fail-count gate.
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }

    /// <summary>GREEN (unchanged run): a baseline exactly matching the actual count never fails.</summary>
    [SkippableFact]
    public void MatchingBaseline_DoesNotFail()
    {
        TestArtifacts.SkipIfMissing();
        WriteBaseline(TestsBaseline(testsDefault: 2));

        var (output, exit) = RunRunner();

        Assert.Equal(0, exit);
        Assert.DoesNotContain("[count-baseline] DROP", output);
        Assert.DoesNotContain("[count-baseline] GROWTH", output);
    }

    /// <summary>
    /// RED (growth): --count-baseline is an EXACT match, not a floor (PR #1882
    /// review). A baseline BELOW the actual count must ALSO fail — a "growth never
    /// fails" rule prints a notice on an otherwise-green run that nobody reads, the
    /// baseline goes stale, and a LATER real drop can land above the stale (too-low)
    /// number and pass unnoticed. So growth exits 4 too, naming expected vs actual,
    /// and (mirroring the drop test above) the underlying tests must NOT have failed
    /// — this exit code is attributable ONLY to the count guard.
    /// </summary>
    [SkippableFact]
    public void Growth_AboveExpected_Exits4WithSuiteExpectedAndActual()
    {
        TestArtifacts.SkipIfMissing();
        WriteBaseline(TestsBaseline(testsDefault: 1));

        var (output, exit) = RunRunner();

        Assert.Equal(4, exit);
        Assert.DoesNotContain("[count-baseline] DROP", output);
        Assert.Contains("[count-baseline] GROWTH", output);
        Assert.Contains($"suite '{_suiteKey}'", output);
        Assert.Contains("expected 1", output);
        Assert.Contains("actual 2", output);
        Assert.Contains(_baselinePath, output);
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }

    /// <summary>
    /// Negative direction of the drop scenario: the SAME baseline that fails the
    /// whole bundle (3 != 2) must stand down when --test intentionally narrows scope
    /// to one test — mirrors the real xmlport-isolation CI leg, which filters the
    /// SAME al-language root a baseline is sized for. Without this, adding
    /// --count-baseline to CI would break that leg.
    /// </summary>
    [SkippableFact]
    public void FilteredRun_SkipsTheGuardEvenWhenBaselineWouldOtherwiseMismatch()
    {
        TestArtifacts.SkipIfMissing();
        WriteBaseline(TestsBaseline(testsDefault: 3));

        var (output, exit) = RunRunner("--test FirstAlwaysPasses");

        Assert.Equal(0, exit);
        Assert.Contains("[count-baseline] skipped", output);
        Assert.DoesNotContain("[count-baseline] DROP", output);
    }

    /// <summary>
    /// The app-group expected count is a SEPARATE metric from the test-count one
    /// (#1880's "strongly consider flooring the app-group / bundle count too"). This
    /// fixture is one app.json (appGroups=1); an expected count of 2 must drop it
    /// independently of the tests count, which is absent from this baseline entirely.
    /// </summary>
    [SkippableFact]
    public void Drop_AppGroupsBelowExpected_Exits4()
    {
        TestArtifacts.SkipIfMissing();
        WriteBaseline(AppGroupsBaseline(appGroupsDefault: 2));

        var (output, exit) = RunRunner();

        Assert.Equal(4, exit);
        Assert.Contains("[count-baseline] DROP", output);
        Assert.Contains("appGroups", output);
        Assert.Contains("expected 2", output);
        Assert.Contains("actual 1", output);
    }

    /// <summary>
    /// BucketResult.RanGroupCount means app groups in bundled mode but SUITES under
    /// --per-suite (see Reporter.cs), so an `appGroups` baseline recorded for bundled
    /// mode is not a meaningful number under --per-suite. The SAME baseline that
    /// would DROP in bundled mode (expected 2, actual 1) must stand down instead of
    /// firing under --per-suite — proves the guard does not silently compare
    /// suite-count-as-if-it-were-app-group-count across modes.
    /// </summary>
    [SkippableFact]
    public void PerSuiteMode_SkipsTheAppGroupsMetric_EvenWhenBaselineWouldOtherwiseDrop()
    {
        TestArtifacts.SkipIfMissing();
        WriteBaseline(AppGroupsBaseline(appGroupsDefault: 2));

        var (output, exit) = RunRunner("--per-suite");

        Assert.Equal(0, exit);
        Assert.Contains("[count-baseline] appGroups check skipped", output);
        Assert.DoesNotContain("[count-baseline] DROP", output);
        Assert.DoesNotContain("[count-baseline] GROWTH", output);
    }
}
