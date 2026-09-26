// TestPageDeletedRowCloseTests — issue #4727.
//
// RUNNER-MECHANISM test for LiveNavTestPage.CloseIfCurrentRowDeleted. The BC claim is upstream
// (corpus codeunit 67300, the Corpus-PR line on the PR that added this file): once an action on
// a Card returns and the Card's stored row is gone, the TestPage is no longer open. This pins
// the runner's wiring: the check runs after every action, only a Card closes, and a row the
// table still holds leaves the page open.
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
    public void AnActionThatLeavesTheCardRowDeleted_ClosesTheCard_AndOnlyTheCard()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm asserts inside AL; the counts separate "passed" from "discovered nothing".
        Assert.True(output.Contains("passed 7 "),
            $"expected all seven arms to pass; exit={exit}\n{output}");
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

        File.WriteAllText(Path.Combine(_root, "Test.Codeunit.al"), """
            codeunit 90483 "TDR Test"
            {
                Subtype = Test;
                var
                    Trace: Codeunit "TDR Trace";

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

                // Only a Card closes. A List moves to a neighbour on BC (#4747); the runner does
                // not yet, but it must not close the List.
                [Test]
                procedure List_ActionDeletesTheRow_StaysOpen()
                var
                    List: TestPage "TDR List";
                    Probe: Text;
                begin
                    Seed();
                    List.OpenEdit();
                    List.GoToKey('A');
                    List.DeleteOnly.Invoke();
                    Probe := List.CodeField.Value();
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
