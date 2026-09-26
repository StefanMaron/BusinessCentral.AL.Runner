// CurrPageUpdateNewRowTests — issue #4698.
//
// RUNNER-MECHANISM test for RunnerPageInstance.EndTrigger's refresh on an UNSAVED new row.
// The BC claim is upstream (the Corpus-PR line on the PR that added this file); this pins the
// runner's wiring: LiveNavTestPage tells its RunnerPageInstance whether the current row is a
// pending insert the table does not hold, and the realised CurrPage.Update refresh then skips
// OnAfterGetRecord for it, exactly as a new row becoming current does.
//
// The shape is Base Application page 9807 "User Card" plus its pageextension 9807: a
// DelayedInsert card whose OnAfterGetCurrRecord calls CurrPage.Update(false), and whose
// OnAfterGetRecord runs Rec.TestField on a field a new row leaves blank.
//
// The fixture declares no "application", per .claude/rules/no-base-app-in-csharp-tests.md.

using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

public sealed class CurrPageUpdateNewRowTests : IDisposable
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private readonly string _root;

    public CurrPageUpdateNewRowTests()
    {
        _root = TestScratch.Dir("al-runner-currpage-update-newrow-4698");
        Directory.CreateDirectory(_root);
        WriteBundle();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void CurrPageUpdate_OnAnUnsavedNewRow_DoesNotRaiseOnAfterGetRecord()
    {
        TestArtifacts.SkipIfMissing();
        var pkg = TestArtifacts.PlatformAppsDir();
        TestArtifacts.SkipIfDirectoryMissing(pkg, "platform apps");

        var (exit, output) = Spawn(_root, pkg);

        // Each arm asserts inside AL; the counts separate "passed" from "discovered nothing".
        Assert.True(output.Contains("passed 3 "),
            $"expected all three arms to pass; exit={exit}\n{output}");
        Assert.Contains("failed 0 ", output);
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
            {
              "id": "3d0e8c61-2b7a-4f45-9e83-4698a0c1d2e5",
              "name": "CurrPage Update New Row Fixture",
              "publisher": "AL Runner Tests",
              "version": "1.0.0.0",
              "dependencies": [],
              "idRanges": [ { "from": 90470, "to": 90479 } ],
              "platform": "27.0.0.0",
              "runtime": "15.0",
              "target": "Cloud"
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Row.Table.al"), """
            table 90470 "NRU Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; Id; Guid) { }
                    field(2; Name; Code[50]) { }
                }
                keys { key(PK; Id) { Clustered = true; } }
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Trace.Codeunit.al"), """
            codeunit 90471 "NRU Trace"
            {
                SingleInstance = true;
                var
                    Order: Text;
                procedure Reset() begin Order := ''; end;
                procedure Note(Tag: Text) begin Order += Tag + ';'; end;
                procedure Get(): Text begin exit(Order); end;
            }
            """);

        // User Card's shape: the key is assigned in OnInsertRecord, the name field's OnValidate
        // saves through CurrPage.Update(), and OnAfterGetCurrRecord asks for a refresh.
        File.WriteAllText(Path.Combine(_root, "Card.Page.al"), """
            page 90472 "NRU Card"
            {
                PageType = Card;
                SourceTable = "NRU Row";
                DelayedInsert = true;
                layout
                {
                    area(Content)
                    {
                        field(IdField; Rec.Id) { ApplicationArea = All; Visible = false; }
                        field(NameField; Rec.Name)
                        {
                            ApplicationArea = All;
                            trigger OnValidate()
                            begin
                                CurrPage.Update();
                            end;
                        }
                    }
                }
                trigger OnInsertRecord(BelowxRec: Boolean): Boolean
                begin
                    Rec.Id := CreateGuid();
                    Rec.TestField(Name);
                end;
                trigger OnAfterGetRecord()
                begin
                    Trace.Note('AGR:' + Rec.Name);
                    Rec.TestField(Name);
                end;
                trigger OnAfterGetCurrRecord()
                begin
                    Trace.Note('AGCR');
                    CurrPage.Update(false);
                end;
                var
                    Trace: Codeunit "NRU Trace";
            }
            """);

        File.WriteAllText(Path.Combine(_root, "Test.Codeunit.al"), """
            codeunit 90473 "NRU Test"
            {
                Subtype = Test;
                var
                    Trace: Codeunit "NRU Trace";

                // The User Card failure itself: OpenNew raised OnAfterGetRecord for the blank
                // row, whose TestField(Name) then failed the open.
                [Test]
                procedure OpenNew_UpdateFromOnAfterGetCurrRecord_RaisesNoOnAfterGetRecord()
                var
                    Card: TestPage "NRU Card";
                begin
                    Trace.Reset();
                    Card.OpenNew();
                    if StrPos(Trace.Get(), 'AGR:') <> 0 then
                        Error('OnAfterGetRecord ran for the unsaved new row: %1', Trace.Get());
                    if StrPos(Trace.Get(), 'AGCR;') = 0 then
                        Error('control: OnAfterGetCurrRecord must run for the new row: %1', Trace.Get());
                    Card.Close();
                end;

                // The other side of the boundary: once CurrPage.Update() has saved the row, the
                // refresh re-reads a row the table holds and OnAfterGetRecord runs for it.
                [Test]
                procedure SetValue_SavedByCurrPageUpdate_RaisesOnAfterGetRecordForTheSavedRow()
                var
                    Card: TestPage "NRU Card";
                begin
                    Card.OpenNew();
                    Trace.Reset();
                    Card.NameField.SetValue('NRU4698');
                    if StrPos(Trace.Get(), 'AGR:NRU4698;') = 0 then
                        Error('OnAfterGetRecord must run for the row CurrPage.Update() saved: %1', Trace.Get());
                    Card.Close();
                end;

                // The row really was written, under the key OnInsertRecord assigned.
                [Test]
                procedure SetValue_SavedByCurrPageUpdate_WritesTheRow()
                var
                    Row: Record "NRU Row";
                    Card: TestPage "NRU Card";
                begin
                    Card.OpenNew();
                    Card.NameField.SetValue('NRU4698B');
                    Card.Close();
                    Row.SetRange(Name, 'NRU4698B');
                    if Row.Count() <> 1 then
                        Error('expected exactly one saved row, got %1', Row.Count());
                    Row.FindFirst();
                    if IsNullGuid(Row.Id) then
                        Error('OnInsertRecord must have assigned the key');
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
