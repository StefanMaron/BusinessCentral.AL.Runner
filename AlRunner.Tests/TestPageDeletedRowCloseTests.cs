// TestPageDeletedRowCloseTests — issue #4727.
//
// RUNNER-MECHANISM test for LiveNavTestPage.CloseIfCurrentRowDeleted. The BC claim is upstream
// (corpus codeunit 67300, the Corpus-PR line on the PR that added this file): once an action on
// a Card returns and the Card's stored row is gone, the TestPage is no longer open. This pins
// the runner's wiring: the check runs after every action, only a Card closes, a List moves to
// the next row, else the previous, else its blank line (#4747), and a row the table still holds,
// or never held, leaves the page where it is.
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageDeletedRowCloseTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public TestPageDeletedRowCloseTests()
    {
        _root = TestScratch.Dir("al-runner-testpage-deleted-row-4727");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void AnActionThatLeavesTheRowDeleted_ClosesACard_AndMovesAList()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm asserts inside AL; the counts separate "passed" from "discovered nothing".
        Assert.True(output.Contains("passed 17 "),
            $"expected all seventeen arms to pass; exit={exit}\n{output}");
        Assert.Contains("failed 0 ", output);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "7a4c2e19-5d31-4b8e-a0f6-4727c0d1e2f3",
              "name": "TestPage Deleted Row Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90480, "to": 90489 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Row.Table.al"), """
            table 90480 "TDR Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; Code; Code[20]) { }
                    field(2; Name; Text[50]) { }
                }
                keys { key(PK; Code) { Clustered = true; } }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Trace.Codeunit.al"), """
            codeunit 90484 "TDR Trace"
            {
                SingleInstance = true;
                var
                    Order: Text;
                procedure Reset() begin Order := ''; end;
                procedure Note(Tag: Text) begin Order += Tag + ';'; end;
                procedure Get(): Text begin exit(Order); end;
            }
            """);

        foreach (var (id, name, type) in new[] { (90481, "TDR Card", "Card"), (90482, "TDR List", "List") })
        {
            var open = type == "List" ? "repeater(Rows) {" : "";
            var close = type == "List" ? "}" : "";
            File.WriteAllText(Path.Combine(_root, $"{type}.Page.al"), $$"""
                page {{id}} "{{name}}"
                {
                    PageType = {{type}};
                    SourceTable = "TDR Row";
                    layout
                    {
                        area(Content)
                        {
                            {{open}}
                            field(CodeField; Rec.Code) { ApplicationArea = All; }
                            field(NameField; Rec.Name) { ApplicationArea = All; }
                            {{close}}
                        }
                    }
                    actions
                    {
                        area(Processing)
                        {
                            action(DeleteOnly)
                            {
                                ApplicationArea = All;
                                trigger OnAction()
                                begin
                                    Rec.Delete();
                                end;
                            }
                            action(DeleteAndUpdate)
                            {
                                ApplicationArea = All;
                                trigger OnAction()
                                begin
                                    Rec.Delete();
                                    CurrPage.Update(false);
                                end;
                            }
                            action(DoNothing)
                            {
                                ApplicationArea = All;
                                trigger OnAction()
                                begin
                                end;
                            }
                        }
                    }
                    trigger OnAfterGetRecord() begin Trace.Note('AGR:' + Rec.Code); end;
                    trigger OnAfterGetCurrRecord() begin Trace.Note('AGCR:' + Rec.Code); end;
                    trigger OnClosePage() begin Trace.Note('ClosePage'); end;
                    var
                        Trace: Codeunit "TDR Trace";
                }
                """);
        }

        // A List whose OnFindRecord answers the first row once DeleteAndPickFirst has run, and is a
        // pass-through Rec.Find(Which) otherwise (DeletePassThrough, #4760).
        File.WriteAllText(Path.Combine(_root, "FindList.Page.al"), """
            page 90485 "TDR Find List"
            {
                PageType = List;
                SourceTable = "TDR Row";
                layout
                {
                    area(Content)
                    {
                        repeater(Rows) { field(CodeField; Rec.Code) { ApplicationArea = All; } }
                    }
                }
                actions
                {
                    area(Processing)
                    {
                        action(DeleteAndPickFirst)
                        {
                            ApplicationArea = All;
                            trigger OnAction()
                            begin
                                PickFirst := true;
                                Rec.Delete();
                            end;
                        }
                        action(DeletePassThrough)
                        {
                            ApplicationArea = All;
                            trigger OnAction()
                            begin
                                Rec.Delete();
                            end;
                        }
                    }
                }
                trigger OnFindRecord(Which: Text): Boolean
                begin
                    Trace.Note('Find:' + Which);
                    if PickFirst then
                        exit(Rec.FindFirst());
                    exit(Rec.Find(Which));
                end;
                trigger OnAfterGetCurrRecord() begin Trace.Note('AGCR:' + Rec.Code); end;
                var
                    Trace: Codeunit "TDR Trace";
                    PickFirst: Boolean;
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Test.Codeunit.al"), """
            codeunit 90483 "TDR Test"
            {
                Subtype = Test;
                var
                    Trace: Codeunit "TDR Trace";

                local procedure FindCalls(Recorded: Text) Calls: Text
                var
                    Entry: Text;
                begin
                    foreach Entry in Recorded.Split(';') do
                        if Entry.StartsWith('Find:') then
                            Calls += Entry + ';';
                end;

                local procedure Seed()
                var
                    Row: Record "TDR Row";
                begin
                    Row.DeleteAll();
                    Row.Code := 'A';
                    Row.Insert();
                    Row.Code := 'B';
                    Row.Insert();
                end;

                [Test]
                procedure Card_ActionDeletesTheRow_FieldReadIsNotOpen()
                var
                    Card: TestPage "TDR Card";
                    Probe: Text;
                begin
                    Seed();
                    Card.OpenEdit();
                    Card.GoToKey('A');
                    Card.DeleteOnly.Invoke();
                    asserterror Probe := Card.CodeField.Value();
                    if GetLastErrorText() <> 'The TestPage is not open.' then
                        Error('expected the Card to be closed, got: %1', GetLastErrorText());
                end;

                [Test]
                procedure Card_ActionDeletesTheRow_CloseIsNotOpen()
                var
                    Card: TestPage "TDR Card";
                begin
                    Seed();
                    Card.OpenEdit();
                    Card.GoToKey('A');
                    Card.DeleteOnly.Invoke();
                    asserterror Card.Close();
                    if GetLastErrorText() <> 'The TestPage is not open.' then
                        Error('expected Close() on the closed Card to raise, got: %1', GetLastErrorText());
                end;

                // Control: the row is still stored, so the Card stays open on it.
                [Test]
                procedure Card_ActionLeavesTheRow_StaysOpen()
                var
                    Card: TestPage "TDR Card";
                begin
                    Seed();
                    Card.OpenEdit();
                    Card.GoToKey('A');
                    Card.DoNothing.Invoke();
                    if Card.CodeField.Value() <> 'A' then
                        Error('expected the Card to stay on A, got: %1', Card.CodeField.Value());
                    Card.Close();
                end;

                // Runner mechanism: the check runs when an action returns, not on a plain field read.
                [Test]
                procedure Card_RowDeletedUnderneath_NoAction_StaysOpen()
                var
                    Row: Record "TDR Row";
                    Card: TestPage "TDR Card";
                begin
                    Seed();
                    Card.OpenEdit();
                    Card.GoToKey('A');
                    Row.Get('A');
                    Row.Delete();
                    if Card.CodeField.Value() <> 'A' then
                        Error('expected the Card to still show A, got: %1', Card.CodeField.Value());
                    Card.Close();
                end;

                // The close raises OnClosePage, and the CurrPage.Update refresh raises nothing for
                // the deleted row: corpus 67300 reads no AGR:A and no AGCR after the action.
                [Test]
                procedure Card_ActionDeletesAndUpdates_RaisesOnlyOnClosePage()
                var
                    Card: TestPage "TDR Card";
                begin
                    Seed();
                    Card.OpenEdit();
                    Card.GoToKey('A');
                    Trace.Reset();
                    Card.DeleteAndUpdate.Invoke();
                    if Trace.Get() <> 'ClosePage;' then
                        Error('expected ClosePage; after the action, got: %1', Trace.Get());
                end;

                // The untouched row OpenNew starts is not in the table, and is not deleted either:
                // an action on it leaves the Card open (corpus 67300).
                [Test]
                procedure Card_OpenNew_ActionOnTheUntouchedRow_StaysOpen()
                var
                    Row: Record "TDR Row";
                    Card: TestPage "TDR Card";
                begin
                    Row.DeleteAll();
                    Card.OpenNew();
                    Card.DoNothing.Invoke();
                    if Card.CodeField.Value() <> '' then
                        Error('expected the blank new row, got: %1', Card.CodeField.Value());
                    Card.Close();
                end;

                // A Card that never read a stored row is not closed by an action either: OpenEdit on
                // an empty table shows a blank row the table never held (corpus 67300).
                [Test]
                procedure Card_OpenEdit_EmptyTable_ActionStaysOpen()
                var
                    Row: Record "TDR Row";
                    Card: TestPage "TDR Card";
                begin
                    Row.DeleteAll();
                    Card.OpenEdit();
                    Card.DoNothing.Invoke();
                    if Card.CodeField.Value() <> '' then
                        Error('expected the blank row, got: %1', Card.CodeField.Value());
                    Card.Close();
                end;

                // The same for a filter that matches no stored row (corpus 67300).
                [Test]
                procedure Card_OpenEdit_FilterMatchesNothing_ActionStaysOpen()
                var
                    Card: TestPage "TDR Card";
                begin
                    Seed();
                    Card.OpenEdit();
                    Card.Filter.SetFilter(Code, 'Q');
                    Card.DoNothing.Invoke();
                    if Card.CodeField.Value() = 'A' then
                        Error('expected the Card not to show the filtered-out row A');
                    Card.Close();
                end;

                // Only a Card closes. A List moves to the next row instead (corpus 67300, #4747),
                // raising OnAfterGetRecord/OnAfterGetCurrRecord for it and nothing for the deleted row.
                [Test]
                procedure List_ActionDeletesTheRow_MovesToTheNextRow()
                var
                    List: TestPage "TDR List";
                begin
                    Seed();
                    List.OpenEdit();
                    List.GoToKey('A');
                    Trace.Reset();
                    List.DeleteOnly.Invoke();
                    if List.CodeField.Value() <> 'B' then
                        Error('expected the List on B, got: %1', List.CodeField.Value());
                    if not Trace.Get().EndsWith('AGCR:B;') or (StrPos(Trace.Get(), 'AGR:A;') <> 0) then
                        Error('expected the trace to end AGCR:B; with no AGR:A;, got: %1', Trace.Get());
                    List.Close();
                end;

                // The same through CurrPage.Update(false): the refresh raises nothing for the
                // deleted row, and the move raises the pair once for the row moved to.
                [Test]
                procedure List_ActionDeletesAndUpdates_MovesToTheNextRow()
                var
                    List: TestPage "TDR List";
                begin
                    Seed();
                    List.OpenEdit();
                    List.GoToKey('A');
                    Trace.Reset();
                    List.DeleteAndUpdate.Invoke();
                    if List.CodeField.Value() <> 'B' then
                        Error('expected the List on B, got: %1', List.CodeField.Value());
                    if not Trace.Get().EndsWith('AGCR:B;') or (StrPos(Trace.Get(), 'AGR:A;') <> 0) then
                        Error('expected the trace to end AGCR:B; with no AGR:A;, got: %1', Trace.Get());
                    List.Close();
                end;

                // The last row has no next one, so the List lands on the row before it.
                [Test]
                procedure List_ActionDeletesTheLastRow_MovesToThePreviousRow()
                var
                    List: TestPage "TDR List";
                begin
                    Seed();
                    List.OpenEdit();
                    List.GoToKey('B');
                    List.DeleteOnly.Invoke();
                    if List.CodeField.Value() <> 'A' then
                        Error('expected the List on A, got: %1', List.CodeField.Value());
                    List.Close();
                end;

                // A middle row: the List lands on the row after it, not on the first row.
                [Test]
                procedure List_ActionDeletesAMiddleRow_MovesToTheNextRow()
                var
                    Row: Record "TDR Row";
                    List: TestPage "TDR List";
                begin
                    Seed();
                    Row.Code := 'C';
                    Row.Insert();
                    List.OpenEdit();
                    List.GoToKey('B');
                    List.DeleteOnly.Invoke();
                    if List.CodeField.Value() <> 'C' then
                        Error('expected the List on C, got: %1', List.CodeField.Value());
                    List.Close();
                end;

                // The only row has no neighbour: the List stays open on its blank new-row line.
                [Test]
                procedure List_ActionDeletesTheOnlyRow_ShowsTheBlankLine()
                var
                    Row: Record "TDR Row";
                    List: TestPage "TDR List";
                begin
                    Row.DeleteAll();
                    Row.Code := 'A';
                    Row.Insert();
                    List.OpenEdit();
                    List.GoToKey('A');
                    Trace.Reset();
                    List.DeleteOnly.Invoke();
                    if List.CodeField.Value() <> '' then
                        Error('expected the blank line, got: %1', List.CodeField.Value());
                    if Trace.Get() <> '' then
                        Error('expected no trigger for the deleted row, got: %1', Trace.Get());
                    if not Row.IsEmpty() then
                        Error('expected nothing re-inserted');
                    List.Close();
                end;

                // A List declaring OnFindRecord moves through it: the trigger answers A where the
                // default re-read of a deleted middle row lands on C. BC calls it '=', '=>', '='
                // (corpus 67300 List_DeletedByAction_OnFindRecord_PicksTheRow).
                [Test]
                procedure List_OnFindRecord_ActionDeletesAMiddleRow_MovesWhereTheTriggerSays()
                var
                    Row: Record "TDR Row";
                    List: TestPage "TDR Find List";
                begin
                    Seed();
                    Row.Code := 'C';
                    Row.Insert();
                    List.OpenEdit();
                    List.GoToKey('B');
                    Trace.Reset();
                    List.DeleteAndPickFirst.Invoke();
                    if List.CodeField.Value() <> 'A' then
                        Error('expected the List on A, got: %1; trace %2', List.CodeField.Value(), Trace.Get());
                    if FindCalls(Trace.Get()) <> 'Find:=;Find:=>;Find:=;' then
                        Error('expected OnFindRecord with = then => then =, got: %1', Trace.Get());
                    if not Trace.Get().EndsWith('AGCR:A;') then
                        Error('expected OnAfterGetCurrRecord for A last, got: %1', Trace.Get());
                    List.Close();
                end;

                // A pass-through OnFindRecord answers false to '=' on the deleted key (#4760; corpus 67300
                // List_DeletedByAction_PassThroughFind_*): a middle row lands on the next row.
                [Test]
                procedure List_PassThroughFind_ActionDeletesAMiddleRow_MovesToTheNextRow()
                var
                    Row: Record "TDR Row";
                    List: TestPage "TDR Find List";
                begin
                    Seed();
                    Row.Code := 'C';
                    Row.Insert();
                    List.OpenEdit();
                    List.GoToKey('B');
                    Trace.Reset();
                    List.DeletePassThrough.Invoke();
                    if List.CodeField.Value() <> 'C' then
                        Error('expected the List on C, got: %1; trace %2', List.CodeField.Value(), Trace.Get());
                    if FindCalls(Trace.Get()) <> 'Find:=;Find:=><;Find:=>;Find:=;' then
                        Error('unexpected OnFindRecord sequence: %1', Trace.Get());
                    if not Trace.Get().EndsWith('AGCR:C;') then
                        Error('expected OnAfterGetCurrRecord for C last, got: %1', Trace.Get());
                    List.Close();
                end;

                // The same with nothing after the deleted row: the previous row.
                [Test]
                procedure List_PassThroughFind_ActionDeletesTheLastRow_MovesToThePreviousRow()
                var
                    List: TestPage "TDR Find List";
                begin
                    Seed();
                    List.OpenEdit();
                    List.GoToKey('B');
                    Trace.Reset();
                    List.DeletePassThrough.Invoke();
                    if List.CodeField.Value() <> 'A' then
                        Error('expected the List on A, got: %1; trace %2', List.CodeField.Value(), Trace.Get());
                    if FindCalls(Trace.Get()) <> 'Find:=;Find:=><;Find:=>;Find:=;' then
                        Error('unexpected OnFindRecord sequence: %1', Trace.Get());
                    if not Trace.Get().EndsWith('AGCR:A;') then
                        Error('expected OnAfterGetCurrRecord for A last, got: %1', Trace.Get());
                    List.Close();
                end;

                // Control: the List's row is still stored, so the List stays on it.
                [Test]
                procedure List_ActionLeavesTheRow_StaysOnIt()
                var
                    List: TestPage "TDR List";
                begin
                    Seed();
                    List.OpenEdit();
                    List.GoToKey('A');
                    List.DoNothing.Invoke();
                    if List.CodeField.Value() <> 'A' then
                        Error('expected the List to stay on A, got: %1', List.CodeField.Value());
                    List.Close();
                end;
            }
            """);
    }

    private static (int ExitCode, string Output) Spawn(string bundle, string pkgDir)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append($" \"{bundle}\"");
        args.Append($" --package-cache \"{pkgDir}\"");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        var sb = new StringBuilder();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(240_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (p.ExitCode, sb.ToString());
    }
}
