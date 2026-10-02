// #3458: BC's own NavTestField.ALAssertEquals and CheckError put ITestField.Name into
// "AssertEquals for Field: {0} ..." and "Validation error for Field: {0}, ...". BC answers the
// control's AL name there, not its caption; the BC half is corpus codeunit 67630 (and 60662).
// This pins the runner's ITestField.Name for a page control and for two controls one
// pageextension adds, so the delta lookup has to match on the control id.
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageFieldErrorNamesControlTests
{
    private const string Al = """
        table 63450 "Fen Row"
        {
            fields
            {
                field(1; "No."; Code[20]) { }
                field(2; "Cust Name"; Text[30]) { Caption = 'Field Caption'; }
                field(3; "Cust Count"; Integer) { Caption = 'Count Field Caption'; }
                field(4; "Ext Name"; Text[30]) { Caption = 'Ext Field Caption'; }
                field(5; "Ext Other"; Text[30]) { Caption = 'Ext Other Field Caption'; }
            }
            keys { key(PK; "No.") { Clustered = true; } }
        }

        page 63450 "Fen Card"
        {
            PageType = Card;
            SourceTable = "Fen Row";
            layout
            {
                area(Content)
                {
                    field(NoCtl; Rec."No.") { }
                    field(CustNameCtl; Rec."Cust Name") { Caption = 'Control Caption'; }
                    field(CustCountCtl; Rec."Cust Count") { Caption = 'Count Control Caption'; }
                    field(VarCtl; MyVar) { Caption = 'Var Caption'; }
                }
            }

            trigger OnOpenPage()
            begin
                MyVar := 'Delta';
            end;

            var
                MyVar: Text[30];
        }

        pageextension 63451 "Fen Card Ext" extends "Fen Card"
        {
            layout
            {
                addlast(Content)
                {
                    field(ExtOtherCtl; Rec."Ext Other") { Caption = 'Ext Other Control Caption'; }
                    field(ExtNameCtl; Rec."Ext Name") { Caption = 'Ext Control Caption'; }
                }
            }
        }

        codeunit 63452 "Fen Tests"
        {
            Subtype = Test;

            local procedure OpenSeeded(var Card: TestPage "Fen Card")
            var
                Row: Record "Fen Row";
            begin
                Row.DeleteAll();
                Row."No." := 'FEN';
                Row."Cust Name" := 'Alpha';
                Row."Cust Count" := 7;
                Row."Ext Name" := 'Beta';
                Row."Ext Other" := 'Gamma';
                Row.Insert();
                Card.OpenEdit();
                Card.GoToRecord(Row);
            end;

            local procedure Expect(Expected: Text)
            begin
                if StrPos(GetLastErrorText(), Expected) = 0 then
                    Error('WRONG: expected [%1] in [%2]', Expected, GetLastErrorText());
                if StrPos(GetLastErrorText(), 'Caption') > 0 then
                    Error('WRONG: a caption is named in [%1]', GetLastErrorText());
            end;

            [Test]
            procedure PageControl_AssertEquals()
            var
                Card: TestPage "Fen Card";
            begin
                OpenSeeded(Card);
                asserterror Card.CustNameCtl.AssertEquals('Wrong');
                Expect('AssertEquals for Field: CustNameCtl Expected = ''Wrong'', Actual = ''Alpha''');
            end;

            [Test]
            procedure PageControl_ValidationError()
            var
                Card: TestPage "Fen Card";
            begin
                OpenSeeded(Card);
                asserterror Card.CustCountCtl.SetValue('not a number');
                Expect('Validation error for Field: CustCountCtl,');
            end;

            [Test]
            procedure ExtensionControl_AssertEquals()
            var
                Card: TestPage "Fen Card";
            begin
                OpenSeeded(Card);
                asserterror Card.ExtNameCtl.AssertEquals('Wrong');
                Expect('AssertEquals for Field: ExtNameCtl Expected = ''Wrong'', Actual = ''Beta''');
            end;

            [Test]
            procedure SecondExtensionControl_AssertEquals()
            var
                Card: TestPage "Fen Card";
            begin
                OpenSeeded(Card);
                asserterror Card.ExtOtherCtl.AssertEquals('Wrong');
                Expect('AssertEquals for Field: ExtOtherCtl Expected = ''Wrong'', Actual = ''Gamma''');
            end;

            [Test]
            procedure PageVariableControl_AssertEquals()
            var
                Card: TestPage "Fen Card";
            begin
                // #4911: a control bound to a page variable is named by its control, not the variable.
                OpenSeeded(Card);
                asserterror Card.VarCtl.AssertEquals('Wrong');
                Expect('AssertEquals for Field: VarCtl Expected = ''Wrong'', Actual = ''Delta''');
                if StrPos(GetLastErrorText(), 'MyVar') > 0 then
                    Error('WRONG: the variable is named in [%1]', GetLastErrorText());
                Card.VarCtl.AssertEquals('Delta');
            end;
        }
        """;

    [SkippableFact]
    public async Task FieldErrors_NameTheControl_NotItsCaption()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-3458-field-error-control-name");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.json"), $$"""
        { "id": "{{Guid.NewGuid()}}", "name": "Fen", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [], "idRanges": [ { "from": 63450, "to": 63459 } ], "runtime": "14.0" }
        """);
        File.WriteAllText(Path.Combine(root, "Fen.al"), Al);

        var r = await SuiteServer.RunViaServer(root);
        Assert.False(r.Tests.Any(t => t.Message.Contains("WRONG:")), r.ToString());
        Assert.True(r.Total == 5 && r.Passed == 5 && r.Failed == 0, r.ToString());
        Assert.Equal(0, r.ExitCode);
    }
}
