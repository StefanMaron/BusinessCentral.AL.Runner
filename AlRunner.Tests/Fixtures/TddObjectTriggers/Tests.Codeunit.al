// Every test runs one object through one call, so the stubs a test is annotated with say which triggers that call
// starts (#5322). The run is measured in AlRunner.Tests/TddObjectTriggersTests.cs.
codeunit 73010 "OT Tests"
{
    Subtype = Test;

    [RequestPageHandler]
    procedure ReqOk(var P: TestRequestPage "OT Report")
    begin
        P.OK().Invoke();
    end;

    [RequestPageHandler]
    procedure ReqSetOwn(var P: TestRequestPage "OT Report")
    begin
        P.Opt.SetValue(3);
        P.OK().Invoke();
    end;

    [Test]
    procedure Report_RunOnAVariable_StartsTheReportAndItsRequestPage()
    var R: Report "OT Report";
    begin
        R.UseRequestPage(false);
        R.Run();
    end;

    [Test]
    [HandlerFunctions('ReqOk')]
    procedure Report_RunOfANamedReport_StartsTheReportAndItsRequestPage()
    begin
        Report.Run(Report::"OT Report");
    end;

    [Test]
    procedure Report_Execute_StartsTheReportAndItsRequestPage()
    begin
        Report.Execute(Report::"OT Report", '');
    end;

    [Test]
    [HandlerFunctions('ReqSetOwn')]
    procedure Report_HandlerSetsAField_StartsThatFieldsTrigger()
    begin
        Report.Run(Report::"OT Report");
    end;

    [Test]
    [HandlerFunctions('ReqOk')]
    procedure Report_RunById_StartsTheTriggersOfEveryReport()
    begin
        Report.Run(73001);
    end;

    [Test]
    [HandlerFunctions('ReqOk')]
    procedure Report_RunRequestPage_StartsTheRequestPageAndNotTheBody()
    var R: Report "OT Report"; Parameters: Text;
    begin
        Parameters := R.RunRequestPage();
    end;

    [Test]
    procedure Report_OnlyConfigured_StartsOnlyTheInitTrigger()
    var R: Report "OT Report"; Rec: Record "OT Rec";
    begin
        R.UseRequestPage(false);
        R.SetTableView(Rec);
    end;

    [Test]
    procedure Report_ThatCallsNoStub_IsNotAnnotated()
    var R: Report "OT Quiet Report";
    begin
        R.Run();
    end;

    [Test]
    procedure Report_OtherReport_StartsOnlyItsOwnTrigger()
    var R: Report "OT Other Report";
    begin
        R.Run();
    end;

    [Test]
    procedure Query_Open_StartsOnBeforeOpen()
    var Q: Query "OT Query";
    begin
        Q.Open();
        Q.Close();
    end;

    [Test]
    procedure Query_SaveAsOfANamedQuery_StartsOnBeforeOpen()
    var Rs: Record "OT Store"; O: OutStream;
    begin
        Rs.Data.CreateOutStream(O);
        Query.SaveAsCsv(Query::"OT Query", O);
    end;

    [Test]
    procedure Query_SaveAsById_StartsTheTriggersOfEveryQuery()
    var Rs: Record "OT Store"; O: OutStream;
    begin
        Rs.Data.CreateOutStream(O);
        Query.SaveAsCsv(73004, O);
    end;

    [Test]
    procedure Query_OnlyRead_StartsNoTrigger()
    var Q: Query "OT Query"; Name: Text;
    begin
        Name := Q.ColumnName(PK);
        Q.TopNumberOfRows(1);
    end;

    [Test]
    procedure XmlPort_Export_StartsTheExportTriggers()
    var X: XmlPort "OT XmlPort"; Rs: Record "OT Store"; O: OutStream;
    begin
        Rs.Data.CreateOutStream(O);
        X.SetDestination(O);
        X.Export();
    end;

    [Test]
    procedure XmlPort_Import_StartsTheImportTriggersAndTheTablesWrites()
    var X: XmlPort "OT XmlPort"; Rs: Record "OT Store"; O: OutStream; I: InStream;
    begin
        Rs.Data.CreateOutStream(O);
        O.WriteText('<Root><Row><PK>Z1</PK><Qty>1</Qty></Row></Root>');
        Rs.Data.CreateInStream(I);
        X.SetSource(I);
        X.Import();
    end;

    [Test]
    procedure XmlPort_ImportOfAnotherXmlPort_WritesThatXmlPortsTable()
    var X: XmlPort "OT Other XmlPort"; Rs: Record "OT Store"; O: OutStream; I: InStream;
    begin
        Rs.Data.CreateOutStream(O);
        O.WriteText('<Root><Row><PK>S1</PK></Row></Root>');
        Rs.Data.CreateInStream(I);
        X.SetSource(I);
        X.Import();
    end;

    [Test]
    procedure XmlPort_StaticExportById_StartsTheTriggersOfEveryXmlPort()
    var Rs: Record "OT Store"; O: OutStream;
    begin
        Rs.Data.CreateOutStream(O);
        XmlPort.Export(73005, O);
    end;

    [Test]
    procedure XmlPort_OnlyConfigured_StartsOnlyTheInitTrigger()
    var X: XmlPort "OT XmlPort"; Rs: Record "OT Store"; O: OutStream;
    begin
        Rs.Data.CreateOutStream(O);
        X.SetDestination(O);
    end;

    [Test]
    procedure NoObject_IsNotAnnotated()
    var Rec: Record "OT Rec";
    begin
        Rec.PK := 'A';
        Rec.Insert();
    end;
}
