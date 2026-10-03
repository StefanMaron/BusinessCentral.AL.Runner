// #5300 — a `--per-suite` suite that lost an object to a compile error. The emit-retry loop drops the
// object that cannot bind and recompiles the survivors, so the suite's `sources` is non-empty and its final
// round reports nothing; the loop read only those two and ran the survivors with no word of what was
// missing: `PASSED, exit code 0`. The bundled loop answers EMIT-EXCLUDED (the dropped codeunit's [Test]
// procedures reported as SKIPPED, exit 3) when `ExcludedObjectTriage.TriageDrops` clears every drop, and
// refuses to run the module otherwise (#3476); --per-suite now asks the same question through the same
// helper (`ReportNonTddEmitDrops`), with the suite as the unit.
//
// Runner-only claim: how this CLI reports a dropped object, with the bundled run of the same directory as
// the reference. Every fact spawns the real CLI (the wiring is Program.cs's) and shares its runs through a
// Lazy, so each fixture is spawned once however many facts read it.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class PerSuiteEmitExcludedTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private sealed record Spawned(int Exit, string Output)
    {
        /// <summary>The per-test rows (`--show-pass` prints every outcome), as "OUTCOME Codeunit.Method".</summary>
        public IReadOnlyList<string> Rows => Regex
            .Matches(Output, @"^(PASS|SKIP|FAIL|ERROR) +(\S.*?)(?: \(\d+ms\))?\s*$", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
            .OrderBy(r => r, StringComparer.Ordinal).ToList();

        /// <summary>The summary's counts, which carry no time.</summary>
        public string Counts => Regex.Match(Output, @"^Tests: .*?(?=\s+Time:|$)", RegexOptions.Multiline).Value;
    }

    // A variable of a codeunit nobody declares: BC answers AL0185 and drops the object.
    private static string Broken(int id, string name) => $$"""
        codeunit {{id}} "{{name}}"
        {
            Subtype = Test;

            [Test]
            procedure Works()
            var
                Api: Codeunit "PS Excl Does Not Exist";
            begin
                Api.Foo();
            end;
        }
        """;

    private static string Healthy(int id, string name, string method, string body = "") => $$"""
        codeunit {{id}} "{{name}}"
        {
            Subtype = Test;

            [Test]
            procedure {{method}}()
            begin
                {{body}}
            end;
        }
        """;

    private const string BadProfile = """
        profile "PS Excl Bad Profile"
        {
            Caption = 'PS Excl Bad Profile';
            RoleCenter = "PS Excl Nonexistent Page";
        }
        """;

    private static string Suite(string root, string name, int idFrom, params (string File, string Source)[] files)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
        {
          "id": "5300{{idFrom:x4}}-0000-4000-8000-000000000000",
          "name": "{{name}}",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": {{idFrom}}, "to": {{idFrom + 99}} } ],
          "runtime": "14.0"
        }
        """);
        foreach (var (file, source) in files) File.WriteAllText(Path.Combine(dir, file), source);
        return dir;
    }

    private static string NewRoot(string name) => TestScratch.Dir("al-runner-persuite-excluded-" + name);

    // suiteA lost "Zero Probe" (one test) and keeps "Good Probe"; suiteB is healthy.
    private static string DroppedRoot(string tag)
    {
        var root = NewRoot(tag);
        Suite(root, "suiteA", 61000,
            ("Broken.al", Broken(61001, "Zero Probe")),
            ("Good.al", Healthy(61002, "Good Probe", "Fine")));
        Suite(root, "suiteB", 62000, ("Good.al", Healthy(62002, "B Probe", "BFine")));
        return root;
    }

    private static readonly Lazy<(string Root, Spawned PerSuite, Spawned Bundled, Spawned PerSuiteWarm)> Dropped = new(() =>
    {
        var root = DroppedRoot("dropped");
        var cache = TestScratch.Dir("al-runner-persuite-excluded-cache");
        return (root, Run(root, perSuite: true, cache), Run(root, perSuite: false, cache),
            Run(root, perSuite: true, cache));
    });

    // suiteA's survivor reaches the dropped codeunit by its object id, which BC does not check.
    private static readonly Lazy<(string Root, Spawned Run)> Refused = new(() =>
    {
        var root = NewRoot("refused");
        Suite(root, "suiteA", 61000,
            ("Broken.al", Broken(61001, "Zero Probe")),
            ("Good.al", Healthy(61002, "Good Probe", "Fine", "Codeunit.Run(61001);")));
        Suite(root, "suiteB", 62000, ("Good.al", Healthy(62002, "B Probe", "BFine")));
        return (root, Run(root, perSuite: true));
    });

    private static readonly Lazy<(string Root, Spawned Run)> ProfileOnly = new(() =>
    {
        var root = NewRoot("profile");
        Suite(root, "suiteP", 63000,
            ("Bad.Profile.al", BadProfile),
            ("Good.al", Healthy(63001, "P Probe", "PFine")));
        Suite(root, "suiteB", 62000, ("Good.al", Healthy(62002, "B Probe", "BFine")));
        return (root, Run(root, perSuite: true));
    });

    // The shared --jobs fixture of #5256 (one dropped codeunit of 3 tests, 3 healthy of 2), as one suite.
    private static readonly Lazy<Spawned> SharedUnderJobs = new(() =>
    {
        var scratch = TestScratch.Dir("al-runner-persuite-excluded-jobs");
        var fixture = Path.Combine(RepoRoot, "AlRunner.Tests", "Fixtures", "JobsUnitClaimExcluded");
        var (exit, output) = JobsUnitClaimEndToEndTests.RunRunner(
            $"--per-suite --show-pass --cache \"{Path.Combine(scratch, "cache")}\" --jobs 2 \"{fixture}\"",
            lowSplitFloor: true);
        return new Spawned(exit, output);
    });

    /// <summary>
    /// The issue's table: a suite that lost a test codeunit. The survivors run, the dropped codeunit's test
    /// is a SKIPPED row, the dropped suite is named, and the run is exit 3 as the bundled run of the same
    /// directory is. The healthy suite beside it is untouched and not named.
    /// </summary>
    [SkippableFact]
    public void ADroppedTestCodeunit_RunsTheSurvivors_ReportsItsTestSkipped_AndExitsThree_AsTheBundledRunDoes()
    {
        TestArtifacts.SkipIfMissing();
        var (_, perSuite, bundled, _) = Dropped.Value;

        Assert.Equal(3, perSuite.Exit);
        Assert.Equal(
            new[] { "PASS Codeunit61002.Fine", "PASS Codeunit62002.BFine", "SKIP Zero Probe.Works" },
            perSuite.Rows.Select(r => Regex.Replace(r, @"^SKIP .*Zero Probe\.Works$", "SKIP Zero Probe.Works")));
        Assert.Matches(@"^Tests: 3\s+passed 2\s+failed 0\s+errors 0\s+skipped 1$", perSuite.Counts);
        Assert.Contains("suiteA: EMIT-EXCLUDED", perSuite.Output);
        Assert.DoesNotContain("suiteB: EMIT-EXCLUDED", perSuite.Output);
        Assert.Contains("PS Excl Does Not Exist", perSuite.Output); // the AL diagnostic that identified it
        Assert.DoesNotContain("PASSED, exit code 0", perSuite.Output);

        // The reference: the bundled run of the same directory reports the same rows and the same exit.
        Assert.Equal(3, bundled.Exit);
        Assert.Equal(bundled.Rows, perSuite.Rows);
        Assert.Equal(bundled.Counts, perSuite.Counts);
    }

    /// <summary>
    /// A second run against the same --cache root answers the same: --per-suite writes nothing to the
    /// AL-output cache, and a module with a dropped object must never be served as a healthy one, because a
    /// hit would skip the emit and with it the report.
    /// </summary>
    [SkippableFact]
    public void ADroppedTestCodeunit_ColdAndWarm_AnswerTheSame()
    {
        TestArtifacts.SkipIfMissing();
        var (_, cold, _, warm) = Dropped.Value;

        Assert.Equal(3, warm.Exit);
        Assert.Equal(cold.Rows, warm.Rows);
        Assert.Equal(cold.Counts, warm.Counts);
        Assert.Contains("suiteA: EMIT-EXCLUDED", warm.Output);
    }

    /// <summary>
    /// A survivor that reaches the dropped codeunit by id is not run around: nothing of THAT suite runs,
    /// the refusal names the reason and the survivor's file, and the next suite still runs and passes.
    /// </summary>
    [SkippableFact]
    public void ARefusedDrop_RunsNothingOfThatSuite_AndTheNextSuiteStillRuns()
    {
        TestArtifacts.SkipIfMissing();
        var (_, run) = Refused.Value;

        Assert.Equal(3, run.Exit);
        Assert.Contains("The module was NOT run", run.Output);
        Assert.Contains("Good.al", run.Output);
        Assert.DoesNotContain(run.Rows, r => r.EndsWith(".Fine") || r.Contains("Zero Probe"));
        Assert.Equal(new[] { "PASS Codeunit62002.BFine" }, run.Rows);
        Assert.Matches(@"^Tests: 1\s+passed 1\s+failed 0\s+errors 0$", run.Counts);
    }

    /// <summary>
    /// A profile declares no executable AL and no [Test] procedure (#2238), so a suite that lost only a
    /// profile runs and reports no suite error, as the bundled run does.
    /// </summary>
    [SkippableFact]
    public void ADroppedProfileAlone_RunsTheSuiteWithNoSuiteError()
    {
        TestArtifacts.SkipIfMissing();
        var (_, run) = ProfileOnly.Value;

        Assert.Equal(0, run.Exit);
        Assert.Contains("profile object(s) could not be compiled", run.Output);
        Assert.DoesNotContain("SUITE ERRORS", run.Output);
        Assert.Equal(2, run.Rows.Count(r => r.StartsWith("PASS ")));
        Assert.Matches(@"^Tests: 2\s+passed 2\s+failed 0\s+errors 0$", run.Counts);
    }

    /// <summary>
    /// Under `--jobs` every worker compiles the suite and finds the same drop; one claim per dropped
    /// object (#5256) makes one worker report its tests, so the aggregate counts them once, and a worker
    /// that left the drop to its peer is not a failed compile.
    /// </summary>
    [SkippableFact]
    public void ASharedSuiteUnderJobs_CountsTheDroppedTestsOnce_AndIsOnePartialSuite()
    {
        TestArtifacts.SkipIfMissing();
        var run = SharedUnderJobs.Value;

        Assert.Equal(3, run.Exit);
        Assert.Contains("Tests: 9   passed 6   failed 0   errors 0   skipped 3", run.Output);
        Assert.Contains("PARTIAL:     1 bundle(s)", run.Output);
        Assert.DoesNotContain("NOT RUN:", run.Output);
        Assert.DoesNotContain("— COMPILE FAIL ===", run.Output);
    }

    // ── runner invocation ─────────────────────────────────────────────────────────────────

    private static Spawned Run(string root, bool perSuite, string? cache = null)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{root}\" --show-pass");
        if (cache != null) args.Append($" --cache \"{Path.GetFullPath(cache)}\"");
        if (perSuite) args.Append(" --per-suite");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return new Spawned(p.ExitCode, sb.ToString());
    }
}
