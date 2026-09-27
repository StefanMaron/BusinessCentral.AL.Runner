// #4878: a page and a pageextension live in separate AL object-type id spaces, so a
// pageextension may carry its page's own number. The actionref lookup keyed the declaring
// object by number alone, took the extension for the page, and never searched the extension's
// instance for the target action it adds. The BC half is corpus codeunit 67538's
// ExtActionRef_ToExtensionAction_InvokeRunsTheTargetsTrigger.
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageExtensionSharingItsPageNumberActionRefTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    // Page 62800's extension shares its number; page 62801's does not, so a fault in the
    // same-number case cannot hide behind a fixture that fails for another reason.
    private static string Fixture(int pageId, int extensionId) => $$"""
        page {{pageId}} "Arn Page {{pageId}}"
        {
            PageType = Card;
            SourceTable = "Arn Record";
            layout { area(Content) { field(CodeCtl; Rec."Code") { } } }
            actions
            {
                area(Promoted) { }
                area(Processing)
                {
                    action(BaseAct) { trigger OnAction() begin Rec."Ran" := 'BASE-{{pageId}}'; Rec.Modify(); end; }
                }
            }
        }
        pageextension {{extensionId}} "Arn Ext {{pageId}}" extends "Arn Page {{pageId}}"
        {
            actions
            {
                addlast(Processing)
                {
                    action(ExtAct) { trigger OnAction() begin Rec."Ran" := 'EXT-{{pageId}}'; Rec.Modify(); end; }
                }
                addlast(Promoted)
                {
                    actionref(ExtRef; ExtAct) { }
                    actionref(BaseRef; BaseAct) { }
                }
            }
        }
        """;

    private static string TestProcedure(string name, int pageId, string actionRef, string expected) => $$"""
            [Test]
            procedure {{name}}()
            var
                R: Record "Arn Record";
                P: TestPage "Arn Page {{pageId}}";
            begin
                R.DeleteAll();
                R."Code" := 'A';
                R.Insert();
                P.OpenEdit();
                P.GoToRecord(R);
                P.{{actionRef}}.Invoke();
                P.Close();
                R.Get('A');
                if R."Ran" <> '{{expected}}' then Error('WRONG: {{actionRef}} on page {{pageId}} ran %1', R."Ran");
            end;
        """;

    [SkippableFact]
    public void ActionRefToTheExtensionsOwnAction_RunsIt_WhenTheExtensionSharesThePagesNumber()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-pageext-same-number-actionref");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "Arn", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "platform": "1.0.0.0", "idRanges": [ { "from": 62800, "to": 62809 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "Arn.al"),
            """
            table 62800 "Arn Record" { fields { field(1; "Code"; Code[20]) { } field(2; "Ran"; Text[30]) { } } keys { key(PK; "Code") { Clustered = true; } } }
            """
            + Fixture(62800, 62800) + Fixture(62801, 62802)
            + "codeunit 62803 \"Arn Tests\"\n{\n    Subtype = Test;\n"
            + TestProcedure("SameNumber_ExtRefToExtAction", 62800, "ExtRef", "EXT-62800")
            + TestProcedure("SameNumber_ExtRefToBaseAction", 62800, "BaseRef", "BASE-62800")
            + TestProcedure("OtherNumber_ExtRefToExtAction", 62801, "ExtRef", "EXT-62801")
            + TestProcedure("OtherNumber_ExtRefToBaseAction", 62801, "BaseRef", "BASE-62801")
            + "}\n");

        var (output, exitCode) = RunCli($" --no-cache \"{root}\"");
        Assert.True(output.Contains("Tests: 4   passed 4   failed 0"), output);
        Assert.DoesNotContain("WRONG:", output);
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
