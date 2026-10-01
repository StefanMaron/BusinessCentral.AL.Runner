// ActionRunPageModeCreateNoHandlerTests — issue #5005.
//
// RUNNER-MECHANISM test. The BC claim is corpus codeunit 67018's *_Triggers arms
// (StefanMaron/BusinessCentral.AL.Language.Tests#512). This pins the runner's wiring for it:
// RunnerPageInstance.OpenTargetUnattended starts the new record a Create-mode action asked for
// when nothing is bound to answer the page (dropped -> <open>), and only for Create (the Edit
// arm is the control that a no-handler open does not start one regardless of mode).
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using Xunit;

namespace AlRunner.Tests;

public sealed class ActionRunPageModeCreateNoHandlerTests : IDisposable
{
    private readonly string _root;

    public ActionRunPageModeCreateNoHandlerTests()
    {
        _root = TestScratch.Dir("al-runner-action-runpagemode-create-no-handler-5005");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public async Task CreateModeWithNoHandler_StartsTheNewRecord_EditModeDoesNot()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var r = await SuiteServer.RunViaServer(_root);

        // Each arm asserts inside AL; the counts distinguish "passed" from "discovered nothing".
        Assert.True(r.Passed == 3,
            $"expected all three arms to pass; exit={r.ExitCode}\n{r}");
        Assert.Equal(0, r.Failed);
        Assert.Equal(0, r.Errors);
        Assert.Equal(0, r.ExitCode);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "7c1d5e2a-3b94-4f60-a8d1-5005e4b2c9a7",
              "name": "Action RunPageMode Create No Handler Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90760, "to": 90764 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """
            table 90760 "RPMC Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "No."; Code[20]) { }
                    field(2; Name; Text[50]) { }
                }
                keys { key(PK; "No.") { Clustered = true; } }
            }

            codeunit 90761 "RPMC Probe"
            {
                SingleInstance = true;
                var
                    Trail: Text;
                procedure Reset() begin Trail := ''; end;
                procedure Note(TriggerName: Text)
                begin
                    if Trail <> '' then
                        Trail += ',';
                    Trail += TriggerName;
                end;
                procedure Triggers(): Text begin exit(Trail); end;
            }

            page 90762 "RPMC Card"
            {
                PageType = Card;
                SourceTable = "RPMC Row";
                ApplicationArea = All;
                layout
                {
                    area(Content)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                        field(Name; Rec.Name) { ApplicationArea = All; }
                    }
                }
                trigger OnOpenPage()
                var
                    Probe: Codeunit "RPMC Probe";
                begin
                    Probe.Note('open');
                end;

                trigger OnNewRecord(BelowxRec: Boolean)
                var
                    Probe: Codeunit "RPMC Probe";
                begin
                    Probe.Note('new:no=' + Rec."No.");
                end;
            }

            page 90763 "RPMC Host"
            {
                PageType = Card;
                SourceTable = "RPMC Row";
                ApplicationArea = All;
                layout
                {
                    area(Content)
                    {
                        field("No."; Rec."No.") { ApplicationArea = All; }
                    }
                }
                actions
                {
                    area(Processing)
                    {
                        action(OpenEdit)
                        {
                            ApplicationArea = All;
                            RunObject = page "RPMC Card";
                            RunPageMode = Edit;
                        }
                        action(OpenCreate)
                        {
                            ApplicationArea = All;
                            RunObject = page "RPMC Card";
                            RunPageMode = Create;
                        }
                    }
                }
            }

            codeunit 90764 "RPMC Tests"
            {
                Subtype = Test;

                local procedure OpenHost(var Host: TestPage "RPMC Host")
                var
                    Row: Record "RPMC Row";
                    Probe: Codeunit "RPMC Probe";
                begin
                    Probe.Reset();
                    Row.DeleteAll();
                    Row."No." := 'A'; Row.Name := 'Alpha'; Row.Insert();
                    Row."No." := 'B'; Row.Name := 'Bravo'; Row.Insert();
                    Host.OpenEdit();
                    Host.GoToKey('B');
                end;

                local procedure Check(Expected: Text; Actual: Text; What: Text)
                begin
                    if Expected <> Actual then
                        Error('%1: expected <%2>, got <%3>', What, Expected, Actual);
                end;

                // Fails with <open> when the no-handler path drops the Create mark.
                [Test]
                procedure CreateNoHandler_RunsOnNewRecordAfterOnOpenPage()
                var
                    Host: TestPage "RPMC Host";
                    Probe: Codeunit "RPMC Probe";
                begin
                    OpenHost(Host);
                    Host.OpenCreate.Invoke();
                    Host.Close();
                    Check('open,new:no=', Probe.Triggers(), 'Create with no handler');
                end;

                // Fails with a third row when the started record is saved on the forced close.
                [Test]
                procedure CreateNoHandler_InsertsNothing()
                var
                    Row: Record "RPMC Row";
                    Host: TestPage "RPMC Host";
                begin
                    OpenHost(Host);
                    Host.OpenCreate.Invoke();
                    Host.Close();
                    Check('2', Format(Row.Count()), 'rows after a Create-mode open with no handler');
                end;

                // Control: fails with <open,new:no=> if every no-handler open started a record.
                [Test]
                procedure EditNoHandler_RunsOnlyOnOpenPage()
                var
                    Host: TestPage "RPMC Host";
                    Probe: Codeunit "RPMC Probe";
                begin
                    OpenHost(Host);
                    Host.OpenEdit.Invoke();
                    Host.Close();
                    Check('open', Probe.Triggers(), 'Edit with no handler');
                end;
            }
            """);
    }
}
