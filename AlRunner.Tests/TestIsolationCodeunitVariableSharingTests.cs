// TestIsolationCodeunitVariableSharingTests — proves that `--isolation codeunit` and
// `--isolation test` are genuinely different modes, on the half that is about the
// codeunit INSTANCE rather than the database.
//
// Under `codeunit` every [Test] in one codeunit runs on the SAME codeunit instance, so
// an AL global variable one test sets is visible to the next. Under `test` every [Test]
// gets a brand new instance, so it is not.
//
// The fixture has two [Test] procedures declared in this order:
//   Step1_IncrementsCounter increments a global Integer from its default (0) to 1.
//   Step2_ExpectsFreshCounter asserts the counter is UNCONDITIONALLY 0.
// So under `codeunit` Step2 sees 1 and FAILS; under `test`/`method` it sees 0 and
// passes. That asymmetry is the whole proof, and it is built on a plain Integer that
// never touches a Record, so the database reset cannot be what makes it pass or fail.
//
// On what this file claims: it asserts what the RUNNER's two modes do, which is a
// runner-specific claim and belongs here. The matching claim about BC is proved where
// it has to be, against a real service tier — corpus codeunit 60898
// "Test Isolation Global Var" raises a global in one [Test] and reads it in the next,
// and is green on BC 27.5 and 28.3. So sharing the instance under `codeunit` is
// faithful to BC, and this file's asymmetry is the runner-side proof of it.
//
// #2144 asserted the same thing from a BC codeunit 130452 that does not exist, and its
// sibling claim about the database turned out backwards when a service tier was finally
// asked (#2160). The conclusion happened to survive; the way it was reached did not.
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
        _root = Path.Combine(Path.GetTempPath(), "al-runner-isolation-variable-sharing", Guid.NewGuid().ToString("N"));
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
          "application": "1.0.0.0",
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
    /// Positive/contrast: `--isolation codeunit` must still share the AL global
    /// variable across both tests in the codeunit — that is precisely what continues
    /// to distinguish it from `test`/`method` now that the database resets the same
    /// way under all three. Step2's unconditional "Counter must be 0" assertion is
    /// FALSE here (Counter is 1, carried over from Step1), so the run fails with a
    /// concrete, checkable message.
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
    /// Negative direction of the same claim: `--isolation test` gives every [Test] a
    /// fresh codeunit instance, so Step2 never sees Step1's increment and both tests
    /// pass. Proves the fixture is not vacuously failing regardless of mode.
    /// </summary>
    [SkippableFact]
    public void IsolationTest_DoesNotShareGlobalVariableAcrossTestMethods()
    {
        TestArtifacts.SkipIfMissing();

        var (output, exit) = RunRunner("--isolation test");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("FAIL  Codeunit", output);
        Assert.Contains("Step1_IncrementsCounter", output);
        Assert.Contains("Step2_ExpectsFreshCounter", output);
    }
}
