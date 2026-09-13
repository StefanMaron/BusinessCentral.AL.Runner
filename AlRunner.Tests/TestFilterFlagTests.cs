// TestFilterFlagTests — proving coverage for the `--test` / `--filter` CLI substring
// filter (TestExecutor.TestFilter → NormaliseFilter/CodeunitMatchesFilter/
// MethodMatchesFilter in AlRunner/TestExecutor.cs), added while closing #1761.
//
// #1761 found AlRunner/TestFilter.cs — a *different*, unrelated record type harvested
// from protocol-v2 (#1607/#1641) with no consumer — dead, and removed it. While
// confirming that, this filter path (the CLI's ACTUAL request-scoping mechanism) turned
// out to have zero unit coverage of its own: nothing exercised NormaliseFilter's
// wildcard-stripping/case-folding or the codeunit-name-vs-method-name match branches in
// CodeunitMatchesFilter/MethodMatchesFilter. This file closes that gap so the surviving
// filter mechanism is provably correct, not just presumed so because it compiled.
//
// IMPORTANT, verified empirically (not assumed): CodeunitMatchesFilter's "codeunit name"
// branch matches against the CLR TYPE name the runner emits for a codeunit — literally
// "Codeunit<ObjectId>" (e.g. "Codeunit62142") — NOT the AL object display name ("TF Alpha
// Tests"). Reporter also prints that CLR type name, not the display name. So a filter
// substring that is meant to hit the "codeunit" branch has to target the object id, and a
// human AL-name-shaped filter (e.g. "Alpha") only ever matches via the METHOD name branch
// unless the object id itself happens to contain the substring. The two fixture tests
// below are written to hit each branch independently, on purpose:
//   "62142"    → codeunit-name (CLR type name) branch only — not present in any method name
//   "Alpha"    → method-name branch only — "Alpha" is not a substring of "Codeunit62142"
//
// Ghost-test trap avoided: each assertion below checks BOTH that the targeted test ran
// AND that the other codeunit's test did NOT run. A no-op filter (e.g. TestFilter parsed
// but never wired into TestExecutor.Run, or a filter that only ever includes everything)
// would make the "did not run" half of every assertion fail.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Used to be serialized with the other runner-subprocess integration tests
// (shared native BC engine state, SIGBUS flakes under xUnit's default
// parallelization) — see DefineFlagIntegrationTests; no longer is — #1809.
public sealed class TestFilterFlagTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;
    private string? _jobsRoot;

    public TestFilterFlagTests()
    {
        _root = TestScratch.Dir("al-runner-test-filter-flag");
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        if (_jobsRoot != null) try { Directory.Delete(_jobsRoot, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a minimal AL package to <paramref name="dir"/>:
    ///   - app.json (no dependencies, id range 62140..62149)
    ///   - a tiny local Assert codeunit (no System App dependency needed)
    ///   - codeunit 62142 "TF Alpha Tests", one [Test] procedure AlphaCheck
    ///   - codeunit 62143 "TF Beta Tests", one [Test] procedure BetaCheck
    /// Object ids 62142/62143 share no substring with the other's method name, and the
    /// method names ("AlphaCheck"/"BetaCheck") share no substring with either object id,
    /// so "62142" and "Alpha" each exercise exactly one of CodeunitMatchesFilter's two
    /// match branches (see file header).
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "b2c3d4e5-f6a7-8901-2345-67890abcdef1",
          "name": "Test Filter Flag Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62140, "to": 62149 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Assert.Codeunit.al"), """
        codeunit 62141 "TFF Assert"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Expected:<%1> Actual:<%2> %3', Expected, Actual, Msg);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "AlphaTest.Codeunit.al"), """
        codeunit 62142 "TF Alpha Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "TFF Assert";

            [Test]
            procedure AlphaCheck()
            begin
                Assert.AreEqual(2, 1 + 1, 'alpha sanity');
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "BetaTest.Codeunit.al"), """
        codeunit 62143 "TF Beta Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "TFF Assert";

            [Test]
            procedure BetaCheck()
            begin
                Assert.AreEqual(4, 2 + 2, 'beta sanity');
            end;
        }
        """);
    }

    private (string output, int exit) RunRunner(params string[] extraArgs)
        => RunRunnerOn(new[] { _root }, extraArgs);

    private (string output, int exit) RunRunnerOn(string[] bundles, params string[] extraArgs)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --strict");
        foreach (var b in bundles) args.Append($" \"{b}\"");
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
        p.ErrorDataReceived  += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>
    /// Sanity control: with no `--test` flag, both codeunits run. Establishes the
    /// baseline the filtered cases below are contrasted against.
    /// </summary>
    [SkippableFact]
    public void NoFilter_BothCodeunitsRun()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner();

        Assert.Equal(0, exit);
        Assert.Contains("Codeunit62142.AlphaCheck", output);
        Assert.Contains("Codeunit62143.BetaCheck", output);
    }

    /// <summary>
    /// Positive: `--test 62142` matches via CodeunitMatchesFilter's own codeunit-name
    /// (CLR type name) check — "62142" is not a substring of the method name
    /// "AlphaCheck", so this can only pass via that branch. Negative in the same
    /// assertion: Beta ("Codeunit62143") must NOT run.
    /// </summary>
    [SkippableFact]
    public void TestFlag_CodeunitTypeNameSubstring_RunsOnlyMatchingCodeunit()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test 62142");

        Assert.Equal(0, exit);
        Assert.Contains("Codeunit62142.AlphaCheck", output);
        Assert.DoesNotContain("Codeunit62143.BetaCheck", output);
    }

    /// <summary>
    /// Positive: `--test Alpha` is not a substring of the CLR type name "Codeunit62142",
    /// so a match here can only come from MethodMatchesFilter's separate "qualified name
    /// OR bare method name" check on "AlphaCheck". Negative in the same assertion: Beta
    /// must NOT run — a no-op filter (accept-everything) would fail that half.
    /// </summary>
    [SkippableFact]
    public void TestFlag_MethodNameSubstring_RunsOnlyMatchingCodeunit()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test Alpha");

        Assert.Equal(0, exit);
        Assert.Contains("Codeunit62142.AlphaCheck", output);
        Assert.DoesNotContain("Codeunit62143.BetaCheck", output);
    }

    /// <summary>
    /// Contrast case for the one above, proving the filter is not just "always match
    /// the first codeunit": `--test Beta` flips which codeunit runs.
    /// </summary>
    [SkippableFact]
    public void TestFlag_MethodNameSubstring_OtherCodeunit_RunsOnlyThatOne()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test Beta");

        Assert.Equal(0, exit);
        Assert.Contains("Codeunit62143.BetaCheck", output);
        Assert.DoesNotContain("Codeunit62142.AlphaCheck", output);
    }

    /// <summary>
    /// Positive: filter matching is case-insensitive (NormaliseFilter lowercases both
    /// the filter and the compared names).
    /// </summary>
    [SkippableFact]
    public void TestFlag_IsCaseInsensitive()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test ALPHA");

        Assert.Equal(0, exit);
        Assert.Contains("Codeunit62142.AlphaCheck", output);
        Assert.DoesNotContain("Codeunit62143.BetaCheck", output);
    }

    /// <summary>
    /// Positive: a leading/trailing '*' is stripped as a shell-ergonomics no-op
    /// (NormaliseFilter), so `--test *Alpha*` behaves identically to `--test Alpha`
    /// rather than being treated as a literal character requiring an exact glob match.
    /// </summary>
    [SkippableFact]
    public void TestFlag_LeadingTrailingWildcard_IsStrippedAsNoOp()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test *Alpha*");

        Assert.Equal(0, exit);
        Assert.Contains("Codeunit62142.AlphaCheck", output);
        Assert.DoesNotContain("Codeunit62143.BetaCheck", output);
    }

    /// <summary>
    /// Negative: a filter matching neither codeunit id nor any method name runs nothing and
    /// fails the run with exit 6 naming the pattern (#4055). It used to exit 0 with
    /// "0 total", so a typo in the pattern read as a clean run. Also proves the filter
    /// does not fall back to "run all" when nothing matches.
    /// </summary>
    [SkippableFact]
    public void TestFlag_NoMatch_RunsNeitherCodeunit_AndFailsWithExit6()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test NoSuchTestExists");

        Assert.Equal(6, exit);
        Assert.DoesNotContain("Codeunit62142.AlphaCheck", output);
        Assert.DoesNotContain("Codeunit62143.BetaCheck", output);
        Assert.Contains("Tests:         0 total", output);
        Assert.Contains("--test 'NoSuchTestExists' selected no test in this run", output);
        Assert.DoesNotContain("interior '*'", output);
    }

    /// <summary>
    /// #4055: the audit counts what the PATTERN selected, not what ran. `Alpha` selects
    /// AlphaCheck and --exclude-test then removes it, so 0 tests run — that is an exclusion, not
    /// a typo, and must stay exit 0 with no "selected no test" line. A count that never
    /// increments would report this run as exit 6.
    /// </summary>
    [SkippableFact]
    public void TestFlag_MatchRemovedByExcludeTest_IsNotANoMatch()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test Alpha", "--exclude-test Codeunit62142.AlphaCheck");

        Assert.True(exit == 0, output);
        Assert.DoesNotContain("PASS  Codeunit62142.AlphaCheck", output);
        Assert.DoesNotContain("Codeunit62143.BetaCheck", output);
        Assert.Contains("Tests:         0 total", output);
        Assert.DoesNotContain("selected no test", output);
    }

    /// <summary>
    /// #4055, the same property under --jobs: each worker's reported count, not its test total,
    /// is what the parent sums.
    /// </summary>
    [SkippableFact]
    public void Jobs_MatchRemovedByExcludeTest_IsNotANoMatch()
    {
        TestArtifacts.SkipIfMissing();
        var (alpha, beta) = WriteTwoBundles();

        var (output, exit) = RunRunnerOn(new[] { alpha, beta },
            "--jobs 2", "--test Alpha", "--exclude-test Codeunit62146.AlphaOnly");

        Assert.True(exit == 0, output);
        Assert.Contains("jobs: 2 bundle(s) across 2 worker process(es)", output);
        Assert.DoesNotContain("PASS  Codeunit62146.AlphaOnly", output);
        Assert.DoesNotContain("selected no test", output);
    }

    /// <summary>
    /// #4055: an interior '*' is matched literally, so `Alpha*Check` selects nothing even
    /// though "AlphaCheck" exists. The failure message says why, which is the part a user
    /// cannot work out from "0 total".
    /// </summary>
    [SkippableFact]
    public void TestFlag_InteriorWildcard_SelectsNothing_AndSaysItIsLiteral()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test \"Alpha*Check\"");

        Assert.Equal(6, exit);
        Assert.DoesNotContain("Codeunit62142.AlphaCheck", output);
        Assert.Contains("--test 'Alpha*Check' selected no test in this run", output);
        Assert.Contains("an interior '*' is matched literally", output);
    }

    /// <summary>
    /// #4055 under --jobs: the zero is about the invocation, not one shard. Two bundles in
    /// two worker processes; `Alpha` matches only the first. Before the parent/worker split a
    /// worker judging its own shard would exit 6 and the parent's worst-of-workers would fail
    /// a legitimate run. A pattern matching in NO shard still fails with 6.
    /// </summary>
    [SkippableFact]
    public void Jobs_PatternMatchingOneShardOnly_Passes_AndMatchingNoShard_Fails()
    {
        TestArtifacts.SkipIfMissing();
        var (alpha, beta) = WriteTwoBundles();

        var (okOutput, okExit) = RunRunnerOn(new[] { alpha, beta }, "--jobs 2", "--test Alpha");
        Assert.True(okExit == 0, okOutput);
        Assert.Contains("jobs: 2 bundle(s) across 2 worker process(es)", okOutput);
        Assert.Contains("Codeunit62146.AlphaOnly", okOutput);
        Assert.DoesNotContain("selected no test", okOutput);

        var (noOutput, noExit) = RunRunnerOn(new[] { alpha, beta }, "--jobs 2", "--test NoSuchTestExists");
        Assert.True(noExit == 6, noOutput);
        Assert.Contains("jobs: 2 bundle(s) across 2 worker process(es)", noOutput);
        Assert.Contains("--test 'NoSuchTestExists' selected no test in this run", noOutput);
    }

    private (string alpha, string beta) WriteTwoBundles()
    {
        // Outside _root: _root is itself a bundle, and nesting these would add them to it.
        _jobsRoot = TestScratch.Dir("al-runner-test-filter-jobs");
        var alpha = Path.Combine(_jobsRoot, "alpha");
        var beta = Path.Combine(_jobsRoot, "beta");
        foreach (var (dir, id, guid, name, method) in new[]
                 {
                     (alpha, 62146, "b2c3d4e5-f6a7-8901-2345-67890abcde46", "TF Jobs Alpha", "AlphaOnly"),
                     (beta, 62147, "b2c3d4e5-f6a7-8901-2345-67890abcde47", "TF Jobs Beta", "BetaOnly"),
                 })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            {
              "id": "{{guid}}",
              "name": "{{name}}",
              "publisher": "AL Runner",
              "version": "1.0.0.0",
              "dependencies": [],
              "platform": "1.0.0.0",
              "idRanges": [ { "from": 62140, "to": 62149 } ],
              "runtime": "14.0"
            }
            """);
            File.WriteAllText(Path.Combine(dir, "Test.Codeunit.al"), $$"""
            codeunit {{id}} "{{name}}"
            {
                Subtype = Test;

                [Test]
                procedure {{method}}()
                begin
                    if 1 + 1 <> 2 then
                        Error('sanity');
                end;
            }
            """);
        }
        return (alpha, beta);
    }

    /// <summary>
    /// `--filter` is documented as a synonym for `--test` (Program.cs:
    /// `args[i] == "--test" || args[i] == "--filter"`). Proves the alias actually wires
    /// to the same TestExecutor.TestFilter, not a dead/ignored flag.
    /// </summary>
    [SkippableFact]
    public void FilterFlag_IsSynonymForTestFlag()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--filter Beta");

        Assert.Equal(0, exit);
        Assert.Contains("Codeunit62143.BetaCheck", output);
        Assert.DoesNotContain("Codeunit62142.AlphaCheck", output);
    }
}
