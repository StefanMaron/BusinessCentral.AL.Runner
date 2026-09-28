// #3479: TryGetControlFormat evaluates the control's Control<id>_Format expression, which runs page
// AL (AutoFormatExpression). A failure there used to be swallowed into "no format declared". It
// now propagates, as TryGetControlCaptionClass's does, and GetValue unwraps the reflection
// wrapper so the AL error itself reaches the test. The BC half is corpus codeunit 67644.
using System.Diagnostics;
using System.Text;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class ControlFormatReadFailureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private sealed class ThrowingExpression
    {
        public NavValue Get() => throw new InvalidOperationException("CFR getter failed");
    }

    [Fact]
    public void GetValue_RethrowsTheGettersOwnException_NotTheReflectionWrapper()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunnerPageInstance.GetValue(new ThrowingExpression()));
        Assert.Equal("CFR getter failed", ex.Message);
    }

    private const string Al = """
        table 63460 "Cfr Row"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; Amount; Decimal) { }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page 63460 "Cfr Card"
        {
            PageType = Card;
            SourceTable = "Cfr Row";
            layout
            {
                area(Content)
                {
                    field(NoCtl; Rec."No.") { }
                    field(PlainCtl; Rec.Amount) { }
                    field(FailingCtl; Rec.Amount)
                    {
                        AutoFormatType = 10;
                        AutoFormatExpression = FailingFormat();
                    }
                }
            }

            local procedure FailingFormat(): Text
            begin
                Error('CFR format expression failed');
            end;
        }

        codeunit 63461 "Cfr Tests"
        {
            Subtype = Test;

            local procedure Seed()
            var
                Row: Record "Cfr Row";
            begin
                Row.DeleteAll();
                Row."No." := 'CFR';
                Row.Amount := 7;
                Row.Insert();
            end;

            local procedure ReadFailing(): Text
            var
                Card: TestPage "Cfr Card";
            begin
                Card.OpenView();
                exit(Card.FailingCtl.Value());
            end;

            [Test]
            procedure FailingFormatExpression_ReachesTheTest()
            begin
                Seed();
                asserterror ReadFailing();
                if StrPos(GetLastErrorText(), 'CFR format expression failed') = 0 then
                    Error('WRONG: the format expression''s error did not reach the test; got [%1]', GetLastErrorText());
            end;

            [Test]
            procedure ControlWithoutFormatExpression_ReadsItsValue()
            var
                Card: TestPage "Cfr Card";
                Shown: Decimal;
            begin
                Seed();
                Card.OpenView();
                Evaluate(Shown, Card.PlainCtl.Value());
                Card.Close();
                if Shown <> 7 then
                    Error('WRONG: the plain control read %1', Shown);
            end;
        }
        """;

    [SkippableFact]
    public void FailingFormatExpression_ErrorReachesTheTest()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-3479-control-format-failure");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "Cfr", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "platform": "1.0.0.0", "idRanges": [ { "from": 63460, "to": 63469 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "Cfr.al"), Al);

        var (output, exitCode) = RunCli($" --no-cache \"{root}\"");
        Assert.False(output.Contains("WRONG:"), output);
        Assert.True(output.Contains("Tests: 2   passed 2   failed 0"), output);
        Assert.Equal(0, exitCode);
    }

    private static (string Output, int ExitCode) RunCli(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = TestBuildConfig.RunArgs(ProjectPath) + TestBuildConfig.BcVersionArg + args,
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
        lock (sb) return (sb.ToString(), p.ExitCode);
    }
}
