// ApplicationAreaControlRemovalTests — issues #4750, #4795, #4829, #4830.
//
// RUNNER-MECHANISM test: pins ApplicationAreaControlRemoval (the Cecil-redirected removal
// pass in MetadataProvider.GetMasterPage, and the MetaReport request-page delegate) and the
// TestPage lookups that consult it (LiveNavTestPage.GetField/GetAction/GetPart,
// RequestPageTestPage.GetField), end to end through a real bundle run. The BC-behaviour claims
// are measured upstream, by corpus codeunits 67530-67533 (pageapplicationarea/).
using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class ApplicationAreaControlRemovalTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public ApplicationAreaControlRemovalTests()
    {
        _root = TestScratch.Dir("al-runner-app-area-control-removal");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static (string output, int exit) RunRunner(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        var platformApps = TestArtifacts.PlatformAppsDir();
        if (Directory.Exists(platformApps)) args.Append($" \"--package-cache\" \"{platformApps}\"");
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

    private void WriteFixture()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "0b4750a1-4750-4750-4750-475047504750",
          "name": "App Area Control Removal Repro",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 90750, "to": 90759 } ],
          "runtime": "17.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Fixture.al"), """
        table 90750 "AACR Record"
        {
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; "Basic Value"; Text[30]) { }
                field(3; "Service Value"; Text[30]) { }
            }
            keys
            {
                key(PK; "Code") { Clustered = true; }
            }
        }

        page 90750 "AACR Card"
        {
            PageType = Card;
            SourceTable = "AACR Record";

            layout
            {
                area(Content)
                {
                    group(General)
                    {
                        field(BasicCtl; Rec."Basic Value")
                        {
                            ApplicationArea = Basic;
                        }
                        field(ServiceCtl; Rec."Service Value")
                        {
                            ApplicationArea = Service;
                        }
                    }
                    part(BasicPart; "AACR Lines")
                    {
                        ApplicationArea = Basic;
                    }
                    part(ServicePart; "AACR Lines")
                    {
                        ApplicationArea = Service;
                    }
                }
            }
            actions
            {
                area(Processing)
                {
                    action(BasicAction)
                    {
                        ApplicationArea = Basic;
                        trigger OnAction()
                        begin
                            Rec."Basic Value" := 'ACT';
                            Rec.Modify();
                        end;
                    }
                    action(ServiceAction)
                    {
                        ApplicationArea = Service;
                        trigger OnAction()
                        begin
                        end;
                    }
                    group(Functions)
                    {
                        action(NestedServiceAction)
                        {
                            ApplicationArea = Service;
                            trigger OnAction()
                            begin
                            end;
                        }
                    }
                }
            }
        }

        page 90752 "AACR Lines"
        {
            PageType = ListPart;
            SourceTable = "AACR Record";

            layout
            {
                area(Content)
                {
                    repeater(Lines)
                    {
                        field(LineCode; Rec."Code")
                        {
                            ApplicationArea = Basic;
                        }
                    }
                }
            }
        }

        report 90753 "AACR Report"
        {
            ProcessingOnly = true;
            UsageCategory = None;

            requestpage
            {
                layout
                {
                    area(Content)
                    {
                        group(Options)
                        {
                            field(BasicOpt; BasicText)
                            {
                                ApplicationArea = Basic;
                            }
                            field(ServiceOpt; ServiceText)
                            {
                                ApplicationArea = Service;
                            }
                        }
                    }
                }
            }

            var
                BasicText: Text[30];
                ServiceText: Text[30];
        }

        codeunit 90751 "AACR Test"
        {
            Subtype = Test;

            var
                ReqPageSeen: Text;

            local procedure MakeRecord(var R: Record "AACR Record")
            begin
                R.DeleteAll();
                R.Init();
                R."Code" := 'A';
                R.Insert();
            end;

            local procedure WriteService(Areas: Text): Text
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
                ReadBack: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea(Areas);
                P.OpenEdit();
                P.GoToRecord(R);
                P.ServiceCtl.SetValue('S');
                ReadBack := P.ServiceCtl.Value();
                P.Close();
                ApplicationArea(Prev);
                exit(ReadBack);
            end;

            [Test]
            procedure AreaNotEnabled_ServiceControlIsNotFound()
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                P.OpenEdit();
                P.GoToRecord(R);
                asserterror P.ServiceCtl.SetValue('S');
                ApplicationArea(Prev);
                if StrPos(GetLastErrorText(), 'is not found on the page.') = 0 then
                    Error('expected the #Service control to be not found, got: %1', GetLastErrorText());
            end;

            [Test]
            procedure AreaNotEnabled_BasicControlIsStillFound()
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                P.OpenEdit();
                P.GoToRecord(R);
                P.BasicCtl.SetValue('B');
                P.Close();
                ApplicationArea(Prev);
                R.Get('A');
                if R."Basic Value" <> 'B' then
                    Error('the #Basic control must write its field, got %1', R."Basic Value");
            end;

            // #4795: actions go through the same pass, and a removed one is BC's own
            // "The action with ID = ... is not found on the page."
            local procedure InvokeServiceActionUnder(Areas: Text; Nested: Boolean): Text
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea(Areas);
                P.OpenEdit();
                P.GoToRecord(R);
                ClearLastError();
                if Nested then
                    asserterror P.NestedServiceAction.Invoke()
                else
                    asserterror P.ServiceAction.Invoke();
                ApplicationArea(Prev);
                exit(GetLastErrorText());
            end;

            [Test]
            procedure AreaNotEnabled_ServiceActionIsNotFound()
            var
                Err: Text;
            begin
                Err := InvokeServiceActionUnder('#Basic,#Suite', false);
                if StrPos(Err, 'The action with ID = ') = 0 then
                    Error('expected the #Service action to be not found, got: %1', Err);
                if StrPos(Err, 'is not found on the page.') = 0 then
                    Error('expected the #Service action to be not found, got: %1', Err);
            end;

            [Test]
            procedure AreaNotEnabled_NestedServiceActionIsNotFound()
            var
                Err: Text;
            begin
                Err := InvokeServiceActionUnder('#Basic,#Suite', true);
                if StrPos(Err, 'The action with ID = ') = 0 then
                    Error('expected the nested #Service action to be not found, got: %1', Err);
            end;

            [Test]
            procedure AreaNotEnabled_BasicActionRuns()
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                P.OpenEdit();
                P.GoToRecord(R);
                P.BasicAction.Invoke();
                P.Close();
                ApplicationArea(Prev);
                R.Get('A');
                if R."Basic Value" <> 'ACT' then
                    Error('the #Basic action must run its OnAction, got %1', R."Basic Value");
            end;

            // #4830: a removed part is BC's own "The part with ID = ... was not found on the page."
            [Test]
            procedure AreaNotEnabled_ServicePartIsNotFound()
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                P.OpenView();
                P.GoToRecord(R);
                asserterror P.ServicePart.First();
                ApplicationArea(Prev);
                if StrPos(GetLastErrorText(), 'was not found on the page.') = 0 then
                    Error('expected the #Service part to be not found, got: %1', GetLastErrorText());
            end;

            [Test]
            procedure AreaNotEnabled_BasicPartIsStillFound()
            var
                R: Record "AACR Record";
                P: TestPage "AACR Card";
                Prev: Text;
                Seen: Text;
            begin
                MakeRecord(R);
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                P.OpenView();
                P.GoToRecord(R);
                P.BasicPart.First();
                Seen := P.BasicPart.LineCode.Value();
                P.Close();
                ApplicationArea(Prev);
                if Seen <> 'A' then
                    Error('the #Basic part must show its row, got %1', Seen);
            end;

            // #4829: the request page goes through the same pass, per RequestFormMetadata read.
            [Test]
            [HandlerFunctions('TouchServiceOpt')]
            procedure AreaNotEnabled_RequestPageServiceControlIsNotFound()
            var
                Prev: Text;
                Params: Text;
            begin
                ReqPageSeen := '';
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                asserterror Params := Report.RunRequestPage(Report::"AACR Report");
                ApplicationArea(Prev);
                if ReqPageSeen <> 'reached' then
                    Error('the request-page handler never reached the #Service control');
                if StrPos(GetLastErrorText(), 'is not found on the page.') = 0 then
                    Error('expected the #Service request-page control to be not found, got: %1', GetLastErrorText());
            end;

            [Test]
            [HandlerFunctions('WriteBasicOpt')]
            procedure AreaNotEnabled_RequestPageBasicControlIsFound()
            var
                Prev: Text;
                Params: Text;
            begin
                ReqPageSeen := '';
                Prev := ApplicationArea();
                ApplicationArea('#Basic,#Suite');
                Params := Report.RunRequestPage(Report::"AACR Report");
                ApplicationArea(Prev);
                if ReqPageSeen <> 'B' then
                    Error('the #Basic request-page control must read back B, got %1', ReqPageSeen);
            end;

            [RequestPageHandler]
            procedure TouchServiceOpt(var RP: TestRequestPage "AACR Report")
            begin
                ReqPageSeen := 'reached';
                RP.ServiceOpt.SetValue('S');
                RP.OK().Invoke();
            end;

            [RequestPageHandler]
            procedure WriteBasicOpt(var RP: TestRequestPage "AACR Report")
            begin
                RP.BasicOpt.SetValue('B');
                ReqPageSeen := RP.BasicOpt.Value();
                RP.OK().Invoke();
            end;

            [Test]
            procedure AreaEnabled_ServiceControlIsFound()
            begin
                if WriteService('#Basic,#Service') <> 'S' then
                    Error('with #Service enabled the control must read back S');
            end;

            [Test]
            procedure EmptyAreaString_ServiceControlIsFound()
            begin
                if WriteService('') <> 'S' then
                    Error('with every area enabled the control must read back S');
            end;
        }
        """);
    }

    [SkippableFact]
    public void TestPage_ControlActionPartOrRequestPageControlWhoseAreaIsNotEnabled_IsNotFound_OthersAre()
    {
        WriteFixture();
        var (output, exit) = RunRunner(_root);
        TestArtifacts.SkipIf(output.Contains("no BC artifact") || output.Contains("[bc] no engines"),
            "no BC engine artifact provisioned in this environment");

        Assert.True(exit == 0, $"expected all eleven AL tests to pass; exit={exit}\n{output}");
        Assert.Contains("   passed 11 ", output);
        Assert.DoesNotContain("FAIL", output);
    }
}
