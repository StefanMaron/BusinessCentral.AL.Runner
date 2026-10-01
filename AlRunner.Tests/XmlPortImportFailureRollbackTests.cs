using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Runner-mechanism test for #4643: a value-consuming <c>XmlPort.Import</c> that fails part-way
/// must roll back the rows it wrote, through the scope the Cecil prepends on BC's
/// <c>Begin/EndTransactionWorldAndTransaction</c> now push and pop (NclCecilRewrite.Forms.cs
/// block 8h). The BC-behaviour claim itself is corpus 60041
/// <c>*GuardedImport_FailingSecondElement_*</c>; this pins the runner's mechanism, including the
/// commit side (a successful import keeps its rows), which the corpus arms do not reach.
///
/// No Library Assert dependency (no "application" in app.json, see
/// .claude/rules/no-base-app-in-csharp-tests.md): each test raises its own Error().
/// </summary>
public class XmlPortImportFailureRollbackTests
{

    [SkippableFact]
    public async Task GuardedImport_FailureRollsBackItsRows_SuccessKeepsThem()
    {
        TestArtifacts.SkipIfMissing();

        var root = TestScratch.Dir("al-runner-xmlport-import-rollback-4643");
        Directory.CreateDirectory(root);

        File.WriteAllText(Path.Combine(root, "app.json"), """
        {
          "id": "b4643000-0000-4000-8000-000000004643",
          "name": "XmlPortImportRollback4643",
          "publisher": "Repro4643",
          "version": "1.0.0.0",
          "dependencies": [],
          "platform": "1.0.0.0",
          "idRanges": [ { "from": 64643, "to": 64649 } ],
          "runtime": "14.0"
        }
        """);

        File.WriteAllText(Path.Combine(root, "XirProbe.al"), """
        table 64643 "XIR Row"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; "Entry No."; Integer) { }
                field(2; Amount; Integer) { }
            }
            keys { key(PK; "Entry No.") { Clustered = true; } }
        }

        table 64644 "XIR Blob"
        {
            DataClassification = SystemMetadata;
            fields
            {
                field(1; Code; Code[10]) { }
                field(2; Data; Blob) { }
            }
            keys { key(PK; Code) { Clustered = true; } }
        }

        xmlport 64645 "XIR Rows"
        {
            Direction = Both;
            Format = Xml;
            UseRequestPage = false;
            schema
            {
                textelement(Root)
                {
                    XmlName = 'Rows';
                    tableelement(Row; "XIR Row")
                    {
                        XmlName = 'Row';
                        fieldelement(EntryNo; Row."Entry No.") { }
                        fieldelement(Amount; Row.Amount) { }
                    }
                }
            }
        }

        codeunit 64646 "XIR Tests"
        {
            Subtype = Test;
            TestPermissions = Disabled;

            local procedure Initialize()
            var
                R: Record "XIR Row";
            begin
                R.DeleteAll();
                R.Init();
                R."Entry No." := 1;
                R.Insert();
                Commit();
            end;

            local procedure Open(var B: Record "XIR Blob" temporary; var InStr: InStream; SecondAmount: Text)
            var
                OutStr: OutStream;
            begin
                B.Init();
                B.Code := 'X';
                B.Insert();
                B.Data.CreateOutStream(OutStr);
                OutStr.WriteText('<?xml version="1.0" encoding="UTF-8"?><Rows>' +
                    '<Row><EntryNo>21</EntryNo><Amount>10</Amount></Row>' +
                    '<Row><EntryNo>22</EntryNo><Amount>' + SecondAmount + '</Amount></Row></Rows>');
                B.Data.CreateInStream(InStr);
            end;

            local procedure Imported(): Integer
            var
                R: Record "XIR Row";
            begin
                R.SetRange("Entry No.", 21, 22);
                exit(R.Count());
            end;

            [Test]
            procedure StaticForm_FailingPayload_RollsBackFirstRow()
            var
                B: Record "XIR Blob" temporary;
                R: Record "XIR Row";
                InStr: InStream;
                Ok: Boolean;
            begin
                Initialize();
                Open(B, InStr, 'NotAnInteger');
                Ok := XmlPort.Import(XmlPort::"XIR Rows", InStr);
                if Ok then
                    Error('XIR1 FAIL: a failing value-consuming import must return false.');
                if Imported() <> 0 then
                    Error('XIR1 FAIL: expected 0 imported rows after a failed import, got %1', Imported());
                if not R.Get(1) then
                    Error('XIR1 FAIL: the caller''s committed row must survive the failed import.');
            end;

            [Test]
            procedure InstanceForm_FailingPayload_RollsBackFirstRow()
            var
                B: Record "XIR Blob" temporary;
                R: Record "XIR Row";
                Xp: XmlPort "XIR Rows";
                InStr: InStream;
                Ok: Boolean;
            begin
                Initialize();
                Open(B, InStr, 'NotAnInteger');
                Xp.SetSource(InStr);
                Ok := Xp.Import();
                if Ok then
                    Error('XIR2 FAIL: a failing value-consuming import must return false.');
                if Imported() <> 0 then
                    Error('XIR2 FAIL: expected 0 imported rows after a failed import, got %1', Imported());
                if not R.Get(1) then
                    Error('XIR2 FAIL: the caller''s committed row must survive the failed import.');
            end;

            [Test]
            procedure StaticForm_ValidPayload_KeepsBothRows()
            var
                B: Record "XIR Blob" temporary;
                InStr: InStream;
                Ok: Boolean;
            begin
                Initialize();
                Open(B, InStr, '20');
                Ok := XmlPort.Import(XmlPort::"XIR Rows", InStr);
                if not Ok then
                    Error('XIR3 FAIL: a valid value-consuming import must return true: %1', GetLastErrorText());
                if Imported() <> 2 then
                    Error('XIR3 FAIL: expected 2 imported rows after a successful import, got %1', Imported());
            end;
        }
        """);

        var r = await SuiteServer.RunViaServer(root);

        Assert.True(r.ExitCode == 0, $"Expected all three import tests to pass (exit 0); got exit {r.ExitCode}.\n{r}");
        // Exit 0 alone would also read a run that executed nothing; pin the count.
        Assert.True(r.Total == 3 && r.Passed == 3 && r.Failed == 0 && r.Errors == 0, $"expected Tests: 3 passed 3 failed 0 errors 0\n{r}");
    }
}
