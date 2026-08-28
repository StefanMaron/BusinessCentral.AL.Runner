// TestIsolationCodeunitVariableSharingTests — RED->GREEN guard for issue #2132.
//
// BC's "Test Runner - Isol. Codeunit" 130450 shares AL GLOBAL VARIABLE state across
// every [Test] procedure in one codeunit but still rolls the DATABASE back between
// them; "Test Runner - Isol. Test" 130452 gives every [Test] a fully fresh codeunit
// instance (neither the database nor a global variable survives). Before #2132 the
// runner's `--isolation codeunit` shared BOTH database rows and global variables
// (TestIsolationMethodAliasTests carried the now-retired database-row proof of that);
// the fix in TestExecutor.cs makes `codeunit` roll the database back per test exactly
// like `test` already did, which raises a real question this file answers: once BOTH
// modes reset the database identically, is there anything left that still tells them
// apart? Yes — this file is that proof, built on a plain AL Integer global variable
// (never touches a Record, so RestoreInstallBaseline()'s database reset can't be the
// thing making it pass or fail either way).
//
// The fixture below has two [Test] procedures declared in this order:
//   Step1_IncrementsCounter increments a global Integer from its default (0) to 1.
//   Step2_ExpectsFreshCounter asserts the counter is UNCONDITIONALLY 0.
// Under `codeunit` isolation the SAME codeunit instance runs both tests, so Step2
// sees Counter = 1 (Step1's increment survived) and the assertion FAILS — the
// ghost-test trap this mirrors: a no-op fix that (wrongly) also resets AL globals
// under `codeunit` isolation would make this test vacuously pass instead of proving
// variable state is genuinely shared. Under `test`/`method` isolation every [Test]
// gets a brand new codeunit instance, so Step2 sees the freshly-constructed default
// (0) and the assertion holds.
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
        codeunit 62132 "Isolation Variable Sharing Tests"
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
