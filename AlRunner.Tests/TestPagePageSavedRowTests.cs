// TestPagePageSavedRowTests — issue #4577. A row the page's own AL saved (CurrPage.SaveRecord from
// OnValidate) is an existing row to the TestPage write buffer, and a part's save is not its
// host's. One AL test per mechanism, so each of the three edits reds exactly one. Issue #4632
// adds New() as a row-leave: InsertEmptyRow's FlushRow reds both *WhenNewLeavesIt arms. The BC
// behaviour is adjudicated upstream by corpus codeunit 60412 "PSR Page Saved Row Tests"; see
// docs/testpage-write-buffer.md#a-row-the-page-saved-itself.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPagePageSavedRowTests : IDisposable
{

    private readonly string _root;

    public TestPagePageSavedRowTests()
    {
        _root = TestScratch.Dir("al-runner-testpage-page-saved-row");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void WriteBundle()
    {
        File.WriteAllText(Path.Combine(_root, "app.json"), """
        {
          "id": "c4d5e6f7-4577-4a7b-8c9d-0e1f2a3b4577",
          "name": "Runner Mechanism - TestPage Page Saved Row",
          "publisher": "AL Runner",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 66810, "to": 66819 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(_root, "Objects.al"), """

        table 66810 "Psr Header"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Code"; Code[20]) { }
                field(2; Descr; Text[30]) { }
                field(3; "Insert Runs"; Integer) { }
            }
            keys { key(PK; "Code") { Clustered = true; } }

            trigger OnInsert()
            begin
                "Insert Runs" += 1;
            end;
        }

        table 66811 "Psr Line"
        {
            DataClassification = CustomerContent;
            fields
            {
                field(1; "Header Code"; Code[20]) { }
                field(2; "Line No."; Integer) { }
                field(3; "Saved Field"; Text[30]) { }
                field(4; "Later Field"; Text[30]) { }
            }
            keys { key(PK; "Header Code", "Line No.") { Clustered = true; } }
        }

        page 66812 "Psr Lines Part"
        {
            PageType = ListPart;
            SourceTable = "Psr Line";
            AutoSplitKey = true;
            DelayedInsert = true;
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    repeater(Lines)
                    {
                        field("Saved Field"; Rec."Saved Field")
                        {
                            ApplicationArea = All;
                            trigger OnValidate()
                            begin
                                CurrPage.SaveRecord();
                            end;
                        }
                        field("Later Field"; Rec."Later Field") { ApplicationArea = All; }
                    }
                }
            }
        }

        page 66813 "Psr Header Card"
        {
            PageType = Card;
            SourceTable = "Psr Header";
            ApplicationArea = All;
            layout
            {
                area(Content)
                {
                    field("Code"; Rec."Code")
                    {
                        ApplicationArea = All;
                        trigger OnValidate()
                        begin
                            CurrPage.SaveRecord();
                        end;
                    }
                    field(Descr; Rec.Descr) { ApplicationArea = All; }
                    part(Lines; "Psr Lines Part")
                    {
                        ApplicationArea = All;
                        SubPageLink = "Header Code" = field("Code");
                    }
                }
            }
        }

        codeunit 66814 "Psr Tests"
        {
            Subtype = Test;

            local procedure Initialize(var Header: Record "Psr Header")
            var
                Line: Record "Psr Line";
            begin
                Line.DeleteAll();
                Header.DeleteAll();
                Header."Code" := 'H1';
                Header.Descr := 'Host';
                Header.Insert();
            end;

            // FlushPendingNewRow: a part row the part's own trigger saved is modified at close.
            [Test]
            procedure PartRowSavedByItsOwnTrigger_KeepsTheValueTypedAfterIt()
            var
                Header: Record "Psr Header";
                Line: Record "Psr Line";
                Card: TestPage "Psr Header Card";
            begin
                Initialize(Header);
                Card.OpenEdit();
                Card.GoToRecord(Header);
                Card.Lines.New();
                Card.Lines."Saved Field".SetValue('A');
                Card.Lines."Later Field".SetValue('B');
                Card.OK().Invoke();
                if Line.Count() <> 1 then
                    Error('lines: %1, expected 1', Line.Count());
                Line.FindFirst();
                if (Line."Saved Field" <> 'A') or (Line."Later Field" <> 'B') then
                    Error('saved=%1 later=%2, expected A and B', Line."Saved Field", Line."Later Field");
            end;

            // RefreshBeforeImageAfterSave: a part's save is not the host's.
            [Test]
            procedure HostEdit_SurvivesAPartSavingItsOwnRow()
            var
                Header: Record "Psr Header";
                Card: TestPage "Psr Header Card";
            begin
                Initialize(Header);
                Card.OpenEdit();
                Card.GoToRecord(Header);
                Card.Descr.SetValue('Changed');
                Card.Lines.New();
                Card.Lines."Saved Field".SetValue('A');
                Card.OK().Invoke();
                Header.Get('H1');
                if Header.Descr <> 'Changed' then
                    Error('Descr=%1, expected Changed', Header.Descr);
            end;

            // InsertEmptyRow (#4632): New() leaves an edited existing row, which saves the edit.
            [Test]
            procedure ExistingPartRowEdit_IsSavedWhenNewLeavesIt()
            var
                Header: Record "Psr Header";
                Line: Record "Psr Line";
                Card: TestPage "Psr Header Card";
            begin
                Initialize(Header);
                Line."Header Code" := 'H1';
                Line."Line No." := 10000;
                Line."Saved Field" := 'X';
                Line.Insert();
                Card.OpenEdit();
                Card.GoToRecord(Header);
                Card.Lines.First();
                Card.Lines."Later Field".SetValue('E');
                Card.Lines.New();
                Card.Lines."Later Field".SetValue('N');
                Card.OK().Invoke();
                if Line.Count() <> 2 then
                    Error('lines: %1, expected 2', Line.Count());
                Line.Get('H1', 10000);
                if Line."Later Field" <> 'E' then
                    Error('existing row later=%1, expected E', Line."Later Field");
                Line.SetRange("Saved Field", '');
                Line.FindFirst();
                if Line."Later Field" <> 'N' then
                    Error('new row later=%1, expected N', Line."Later Field");
            end;

            // InsertEmptyRow (#4632): a row the part saved itself is a Modify when New() leaves it.
            [Test]
            procedure PartRowSavedByItsOwnTrigger_KeepsTheLaterValueWhenNewLeavesIt()
            var
                Header: Record "Psr Header";
                Line: Record "Psr Line";
                Card: TestPage "Psr Header Card";
            begin
                Initialize(Header);
                Card.OpenEdit();
                Card.GoToRecord(Header);
                Card.Lines.New();
                Card.Lines."Saved Field".SetValue('A');
                Card.Lines."Later Field".SetValue('B');
                Card.Lines.New();
                Card.Lines."Saved Field".SetValue('C');
                Card.Lines."Later Field".SetValue('D');
                Card.OK().Invoke();
                if Line.Count() <> 2 then
                    Error('lines: %1, expected 2', Line.Count());
                Line.SetRange("Saved Field", 'A');
                Line.FindFirst();
                if Line."Later Field" <> 'B' then
                    Error('first row later=%1, expected B', Line."Later Field");
                Line.SetRange("Saved Field", 'C');
                Line.FindFirst();
                if Line."Later Field" <> 'D' then
                    Error('second row later=%1, expected D', Line."Later Field");
            end;

            // ActivateControl: a new row the page saved is not inserted again on focus.
            [Test]
            procedure NewHostRowSavedByItsOwnTrigger_IsInsertedOnce()
            var
                Header: Record "Psr Header";
                Card: TestPage "Psr Header Card";
            begin
                Initialize(Header);
                Card.OpenNew();
                Card."Code".SetValue('H2');
                Card.Descr.SetValue('Second');
                Card.Close();
                Header.Get('H2');
                if (Header."Insert Runs" <> 1) or (Header.Descr <> 'Second') then
                    Error('Insert Runs=%1 Descr=%2, expected 1 and Second', Header."Insert Runs", Header.Descr);
            end;
        }
        """);
    }

    [SkippableFact]
    public async Task ARowThePageSavedItself_IsModifiedNotReinserted()
    {
        TestArtifacts.SkipIfMissing();

        WriteBundle();
        var r = await SuiteServer.RunViaServer(_root);

        Assert.True(r.ExitCode == 0, $"Expected the bundle to pass; exit={r.ExitCode}\n{r}");
        foreach (var name in new[]
                 {
                     "PartRowSavedByItsOwnTrigger_KeepsTheValueTypedAfterIt",
                     "HostEdit_SurvivesAPartSavingItsOwnRow",
                     "NewHostRowSavedByItsOwnTrigger_IsInsertedOnce",
                     "ExistingPartRowEdit_IsSavedWhenNewLeavesIt",
                     "PartRowSavedByItsOwnTrigger_KeepsTheLaterValueWhenNewLeavesIt",
                 })
            r.AssertPassed("Codeunit66814." + name);
        r.AssertNoFailures();
    }
}
