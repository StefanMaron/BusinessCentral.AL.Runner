// TestIsolationMethodAliasTests — RED->GREEN guard for issue #1647.
//
// v1's `--test-isolation method` reset AL table/session state before EVERY [Test]
// procedure. v2 kept `method` as a backward-compat alias but pointed it at
// TestIsolation.Codeunit — state shared within a codeunit, reset only between
// codeunits — instead of the mode that actually does a per-test reset
// (TestIsolation.Test). Callers that pass the v1-idiomatic `--test-isolation method`
// (including external tooling built against v1, e.g. the AL mutation-testing tool
// LethAL) silently got weaker isolation than v1 gave them, with no error.
//
// Ghost-test trap avoided: the fixture below has two [Test] procedures in ONE
// codeunit. Step1 inserts a row and commits implicitly at the end of the method
// (BC always commits between test methods). Step2 asserts the row count is
// UNCONDITIONALLY 0 — under `--test-isolation codeunit` that assertion held FALSE
// before the fix below landed, so a no-op fix that keeps `method` pointed at
// Codeunit isolation makes this test FAIL, not vacuously pass.
//
// Database-contrast fact retired: this file used to also carry a fourth fact,
// TestIsolationCodeunit_SharesStateBetweenTestMethods, proving `codeunit` isolation
// was a REAL, distinct mode from `method`/`test` by showing the database row from
// Step1 survived into Step2 under `codeunit` but not under `method`/`test`. Real BC's
// "Test Runner - Isol. Codeunit" 130450 rolls the database back between every test
// inside the codeunit too — only AL GLOBAL VARIABLES stay shared — so that
// database-row discriminator stopped being true once the runner's `codeunit` mode was
// brought in line with BC (issue distinct from #1647; the CLASS's own #1647 concern
// still stands, just needs a discriminator that BC still preserves). See
// TestIsolationCodeunitVariableSharingTests below for the replacement proof, built on
// an AL global variable instead of a database row.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

// Used to be serialized with the other runner-subprocess integration tests
// (shared native BC engine state, SIGBUS flakes under xUnit's default
// parallelization) — see DefineFlagIntegrationTests; no longer is — #1809.
public sealed class TestIsolationMethodAliasTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestIsolationMethodAliasTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-isolation-method-alias", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Writes a minimal AL package to <paramref name="dir"/>:
    ///   - app.json (no dependencies, id range 62110..62119)
    ///   - a one-field table
    ///   - a test codeunit with two [Test] procedures, declared in this order:
    ///       Step1_InsertsRow inserts one row.
    ///       Step2_ExpectsFreshTable asserts the table is empty (UNCONDITIONALLY).
    ///     Under real per-test isolation (v1's `method`, v2's `test`), Step2 always
    ///     sees an empty table because state resets before every [Test]. Under
    ///     `codeunit` isolation the row from Step1 must ALSO be gone now — real BC's
    ///     130450 rolls the database back between tests inside the codeunit — so this
    ///     fixture proves the DATABASE half of the fix for every isolation mode; see
    ///     TestIsolationCodeunitVariableSharingTests for the half that must NOT reset.
    /// </summary>
    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "a1b2c3d4-e5f6-7890-1234-567890abcdef",
          "name": "Isolation Method Alias Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "idRanges": [ { "from": 62110, "to": 62119 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Assert.Codeunit.al"), """
        codeunit 62111 "IMA Assert"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Expected:<%1> Actual:<%2> %3', Expected, Actual, Msg);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Marker.Table.al"), """
        table 62110 "IMA Marker"
        {
            fields
            {
                field(1; Id; Integer) { }
            }
            keys
            {
                key(PK; Id) { Clustered = true; }
            }
        }
        """);

        File.WriteAllText(Path.Combine(dir, "IsolationTest.Codeunit.al"), """
        codeunit 62112 "Isolation Method Alias Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "IMA Assert";

            [Test]
            procedure Step1_InsertsRow()
            var
                Marker: Record "IMA Marker";
            begin
                Marker.Init();
                Marker.Id := 1;
                Marker.Insert();
                Assert.AreEqual(1, Marker.Count(), 'row must be inserted');
            end;

            [Test]
            procedure Step2_ExpectsFreshTable()
            var
                Marker: Record "IMA Marker";
            begin
                Assert.AreEqual(0, Marker.Count(), 'table must be reset before this test — per-test database rollback must have fired');
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
    /// Positive: `--test-isolation method` must behave like v1 — a fresh reset before
    /// every [Test] procedure. Before the fix (method aliased to Codeunit isolation)
    /// Step2 sees the row Step1 inserted and fails; this is the RED proof.
    /// </summary>
    [SkippableFact]
    public void TestIsolationMethod_ResetsStateBetweenTestMethods()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--test-isolation method");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("FAIL  Codeunit", output);
        Assert.Contains("Step1_InsertsRow", output);
        Assert.Contains("Step2_ExpectsFreshTable", output);
    }

    /// <summary>
    /// Same assertion via the short `--isolation` flag spelling, which shares the
    /// same alias-mapping switch.
    /// </summary>
    [SkippableFact]
    public void IsolationMethod_ResetsStateBetweenTestMethods()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--isolation method");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("FAIL  Codeunit", output);
    }

    /// <summary>
    /// #2132: real BC's "Test Runner - Isol. Codeunit" 130450 rolls the DATABASE back
    /// between every test inside the codeunit too, not just between codeunits. Before
    /// the #2132 fix, `--isolation codeunit` left Step1's row in place for Step2 and
    /// this assertion failed (RED). After the fix the row is gone here exactly like it
    /// is under `method`/`test` — the database half of the two modes is now identical;
    /// see TestIsolationCodeunitVariableSharingTests for the half that still differs.
    /// </summary>
    [SkippableFact]
    public void TestIsolationCodeunit_DatabaseNoLongerLeaksBetweenTestMethods()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--isolation codeunit");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("FAIL  Codeunit", output);
        Assert.Contains("Step1_InsertsRow", output);
        Assert.Contains("Step2_ExpectsFreshTable", output);
    }
}
