// TestIsolationCodeunitVariableSharingTests — the codeunit INSTANCE half of the isolation modes:
// under both `--isolation codeunit` and `--isolation test`, every [Test] in one codeunit runs on
// the SAME codeunit instance, so an AL global variable one test sets is visible to the next.
//
// The fixture has two [Test] procedures declared in this order:
//   Step1_IncrementsCounter increments a global Integer from its default (0) to 1.
//   Step2_ExpectsFreshCounter asserts the counter is UNCONDITIONALLY 0.
// So Step2 sees 1 and FAILS in both modes. It is built on a plain Integer that never touches a
// Record, so the database reset cannot be what makes it pass or fail.
//
// BC settles both halves on a real service tier: corpus codeunit 60898 "Test Isolation Global
// Var" for Codeunit isolation (green on 27.5 and 28.3), and corpus PR #517's Function-isolation
// probe for `test` (#4826; T2/T3 green on 28.4.53241.55454, Windows nightly run 36727084058).
// Until #4826 the runner gave every [Test] a fresh instance under `test`, which BC does not.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestIsolationCodeunitVariableSharingTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestIsolationCodeunitVariableSharingTests()
    {
        _root = TestScratch.Dir("al-runner-isolation-variable-sharing");
        Directory.CreateDirectory(_root);
        WriteFixture(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void WriteFixture(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "app.json"), """
        {
          "id": "d4e5f6a7-b8c9-4012-3456-7890abcdef12",
          "name": "Isolation Variable Sharing Test Fixture",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 62130, "to": 62139 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(dir, "Assert.Codeunit.al"), """
        codeunit 62131 "IVS Assert"
        {
            procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
            begin
                if Expected <> Actual then
                    Error('Expected:<%1> Actual:<%2> %3', Expected, Actual, Msg);
            end;
        }
        """);

        File.WriteAllText(Path.Combine(dir, "IsolationTest.Codeunit.al"), """
        codeunit 62132 "Isolation Var Sharing Tests"
        {
            Subtype = Test;

            var
                Assert: Codeunit "IVS Assert";
                Counter: Integer;

            [Test]
            procedure Step1_IncrementsCounter()
            begin
                Assert.AreEqual(0, Counter, 'a freshly-constructed codeunit instance must start at the default');
                Counter += 1;
            end;

            [Test]
            procedure Step2_ExpectsFreshCounter()
            begin
                Assert.AreEqual(0, Counter, 'a fresh test-codeunit instance must start at the default — Step1''s increment must not survive under this isolation mode');
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
    /// `--isolation codeunit` shares the AL global variable across both tests in the
    /// codeunit. Step2's unconditional "Counter must be 0" assertion is FALSE here
    /// (Counter is 1, carried over from Step1), so the run fails with a concrete message.
    /// </summary>
    [SkippableFact]
    public void IsolationCodeunit_SharesGlobalVariableAcrossTestMethods()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--isolation codeunit");

        Assert.NotEqual(0, exit);
        Assert.Contains("Step2_ExpectsFreshCounter", output);
        Assert.Contains("Expected:<0> Actual:<1>", output);
    }

    /// <summary>
    /// `--isolation test` shares the instance too (#4826): BC's TestIsolation = Function runs
    /// every [Test] on one instance and only rolls the database back between them.
    /// </summary>
    [SkippableFact]
    public void IsolationTest_SharesGlobalVariableAcrossTestMethods()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--isolation test");

        Assert.NotEqual(0, exit);
        Assert.Contains("Step2_ExpectsFreshCounter", output);
        Assert.Contains("Expected:<0> Actual:<1>", output);
    }
}
