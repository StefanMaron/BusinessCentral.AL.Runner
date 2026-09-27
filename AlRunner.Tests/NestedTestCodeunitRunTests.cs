using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for #4827: the static <c>Codeunit.Run</c> of a <c>Subtype = Test</c>
/// codeunit reaches BC's own <c>NavTestCodeunit.DoRunAsync</c>, so a nested run is refused by
/// BC's <c>NavTestExecution.EnterTestCodeunit</c> rather than by anything the runner wrote.
///
/// The BC claim (the nested run errors, guarded or not, and runs no inner test) is measured
/// upstream by corpus codeunit 67566 (<c>TestCodeunitRunNestedTestCodeunit.al</c>). What this
/// pins is the wiring: the failure carries BC's own exception type,
/// <c>NavNCLTestCodeUnitNestedInvocationException</c>, which only BC's guard raises, and
/// <c>CodeunitPatches.NavCodeunit_RunCodeunit</c> is still the path a plain codeunit takes.
///
/// No asserterror and no Library Assert: each outer test lets the error reach the runner, so
/// the runner's FAIL line (exception type + message) is the assertion surface.
/// </summary>
public class NestedTestCodeunitRunTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string NestedException = "NavNCLTestCodeUnitNestedInvocationException";

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" \"").Append(bundle).Append('"');
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
        if (!p.WaitForExit(180_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    /// <summary>The line after the FAIL header of <paramref name="method"/>, or null if it did not fail.</summary>
    private static string? FailureDetail(string output, string method)
    {
        var lines = output.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
            if (lines[i].StartsWith("FAIL", StringComparison.Ordinal) && lines[i].Contains("." + method + " "))
                return lines[i + 1].Trim();
        return null;
    }

    [SkippableFact]
    public void StaticRunOfTestCodeunit_FromATest_IsRefusedByBcsOwnGuard()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-nested-test-codeunit-4827");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b4827000-0000-4000-8000-000000004827",
          "name": "NestedTestCodeunit4827",
          "publisher": "Repro4827",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64827, "to": 64830 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "Ntc.al"), """
        codeunit 64827 "NTC4827 Inner Tests"
        {
            Subtype = Test;

            [Test]
            procedure InnerTest_Runs()
            begin
            end;
        }

        codeunit 64828 "NTC4827 Plain"
        {
            trigger OnRun()
            begin
            end;
        }

        codeunit 64829 "NTC4827 Outer Tests"
        {
            Subtype = Test;

            [Test]
            procedure StaticRun_Unguarded()
            begin
                Codeunit.Run(Codeunit::"NTC4827 Inner Tests");
                Error('NTC4827 unguarded nested run was not refused');
            end;

            [Test]
            procedure StaticRun_Guarded()
            var
                Ok: Boolean;
            begin
                Ok := Codeunit.Run(Codeunit::"NTC4827 Inner Tests");
                Error('NTC4827 guarded nested run returned %1', Ok);
            end;

            [Test]
            procedure PlainRun_Succeeds()
            begin
                if not Codeunit.Run(Codeunit::"NTC4827 Plain") then
                    Error('NTC4827 plain Codeunit.Run returned false');
            end;
        }
        """);

        var (output, exitCode) = RunRunner(root);

        Assert.True(exitCode == 1, $"Expected exactly the two nested runs to fail (exit 1); got exit {exitCode}.\n{output}");

        foreach (var method in new[] { "StaticRun_Unguarded", "StaticRun_Guarded" })
        {
            var detail = FailureDetail(output, method);
            Assert.True(detail != null, $"{method} must fail: the nested run is refused.\n{output}");
            Assert.StartsWith(NestedException + ":", detail);
            Assert.Contains("Test codeunit 64827", detail);
        }

        Assert.Null(FailureDetail(output, "PlainRun_Succeeds"));
        Assert.Null(FailureDetail(output, "InnerTest_Runs"));
        Assert.Contains("Tests: 4", output);
        Assert.DoesNotContain("NTC4827 unguarded nested run was not refused", output);
        Assert.DoesNotContain("NTC4827 guarded nested run returned", output);
    }
}
