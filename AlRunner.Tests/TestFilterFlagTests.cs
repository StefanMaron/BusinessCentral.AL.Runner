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

// Serialized with the other runner-subprocess integration tests — see
// DefineFlagIntegrationTests for why (shared native BC engine state, SIGBUS flakes
// under xUnit's default parallelization).
[Collection("server-serial")]
public sealed class TestFilterFlagTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestFilterFlagTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-test-filter-flag", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
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
          "application": "1.0.0.0",
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
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --strict");
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
    /// Negative: a filter matching neither codeunit id nor any method name excludes
    /// everything and still exits 0 — proves the filter can produce an empty result set
    /// rather than silently falling back to "run all" when nothing matches.
    /// </summary>
    [SkippableFact]
    public void TestFlag_NoMatch_RunsNeitherCodeunit()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test NoSuchTestExists");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Codeunit62142.AlphaCheck", output);
        Assert.DoesNotContain("Codeunit62143.BetaCheck", output);
        Assert.Contains("Tests:         0 total", output);
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
