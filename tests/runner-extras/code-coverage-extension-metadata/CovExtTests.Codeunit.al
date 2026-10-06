table 66701 "Cov Ext Tbl"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

tableextension 66702 "Cov Ext Tbl Ext" extends "Cov Ext Tbl"
{
    fields
    {
        field(66700; Extra; Integer) { }
    }

    procedure Twice(Value: Integer): Integer
    begin
        exit(Value * 2);
    end;
}

codeunit 66704 "Cov Ext Probe"
{
    SingleInstance = true;

    var
        Marker: Text;

    procedure Mark(Value: Text)
    begin
        Marker := Value;
    end;

    procedure Read(): Text
    begin
        exit(Marker);
    end;
}

page 66705 "Cov Ext Page"
{
    PageType = Card;
    SourceTable = "Cov Ext Tbl";
    layout
    {
        area(Content)
        {
            field("No."; Rec."No.") { ApplicationArea = All; }
        }
    }
}

pageextension 66706 "Cov Ext Page Ext" extends "Cov Ext Page"
{
    trigger OnOpenPage()
    begin
        MarkFromPageExtension('page-ext-ran');
    end;

    local procedure MarkFromPageExtension(Value: Text)
    var
        Probe: Codeunit "Cov Ext Probe";
    begin
        Probe.Mark(Value);
    end;
}

report 66707 "Cov Ext Report"
{
    ProcessingOnly = true;
    UseRequestPage = false;
    dataset
    {
        dataitem(Item; Integer)
        {
            DataItemTableView = where(Number = const(1));
        }
    }
}

reportextension 66708 "Cov Ext Report Ext" extends "Cov Ext Report"
{
    trigger OnPreReport()
    begin
        MarkFromReportExtension('report-ext-ran');
    end;

    local procedure MarkFromReportExtension(Value: Text)
    var
        Probe: Codeunit "Cov Ext Probe";
    begin
        Probe.Mark(Value);
    end;
}

codeunit 66703 "Cov Ext Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Cov Ext Assert";

    local procedure HitCodeLines(var CodeCoverage: Record "Code Coverage"; Id: Integer): Integer
    begin
        CodeCoverage.SetRange("Object ID", Id);
        CodeCoverage.SetRange("Line Type", CodeCoverage."Line Type"::Code);
        CodeCoverage.SetFilter("No. of Hits", '>0');
        exit(CodeCoverage.Count());
    end;

    [Test]
    procedure TableExtensionProcedure_WhileRecording_ReturnsItsResult()
    var
        Rec: Record "Cov Ext Tbl";
        CodeCoverage: Record "Code Coverage";
        Result: Integer;
    begin
        CodeCoverageLog(true, false);
        Result := Rec.Twice(21);
        CodeCoverageLog(false, false);
        Assert.AreEqual(42, Result, 'the tableextension procedure must run while recording');
        CodeCoverage.SetRange("Object Type", CodeCoverage."Object Type"::TableExtension);
        Assert.IsTrue(HitCodeLines(CodeCoverage, 66702) > 0, 'the tableextension must have a Code line with hits');
    end;

    [Test]
    procedure PageExtensionProcedure_WhileRecording_Runs()
    var
        Probe: Codeunit "Cov Ext Probe";
        CodeCoverage: Record "Code Coverage";
        CovPage: TestPage "Cov Ext Page";
    begin
        Probe.Mark('');
        CodeCoverageLog(true, false);
        CovPage.OpenView();
        CovPage.Close();
        CodeCoverageLog(false, false);
        Assert.AreEqual('page-ext-ran', Probe.Read(), 'the pageextension trigger and procedure must run while recording');
        CodeCoverage.SetRange("Object Type", CodeCoverage."Object Type"::PageExtension);
        Assert.IsTrue(HitCodeLines(CodeCoverage, 66706) > 0, 'the pageextension must have a Code line with hits');
    end;

    [Test]
    procedure ReportExtensionProcedure_WhileRecording_Runs()
    var
        Probe: Codeunit "Cov Ext Probe";
        CodeCoverage: Record "Code Coverage";
    begin
        Probe.Mark('');
        CodeCoverageLog(true, false);
        Report.Run(Report::"Cov Ext Report");
        CodeCoverageLog(false, false);
        Assert.AreEqual('report-ext-ran', Probe.Read(), 'the reportextension trigger and procedure must run while recording');
        CodeCoverage.SetRange("Object Type", CodeCoverage."Object Type"::ReportExtension);
        Assert.IsTrue(HitCodeLines(CodeCoverage, 66708) > 0, 'the reportextension must have a Code line with hits');
    end;

    [Test]
    procedure TableExtensionProcedure_AfterAnEarlierRecording_StillRuns()
    var
        Rec: Record "Cov Ext Tbl";
    begin
        // Recording from an earlier test must not have been left on in a state that fails this call.
        CodeCoverageLog(true, false);
        Assert.AreEqual(10, Rec.Twice(5), 'the tableextension procedure must run while recording');
        CodeCoverageLog(false, false);
        Assert.AreEqual(14, Rec.Twice(7), 'the tableextension procedure must run once recording has stopped');
        Assert.IsFalse(CodeCoverageLog(), 'recording must be off after it was stopped');
    end;
}
