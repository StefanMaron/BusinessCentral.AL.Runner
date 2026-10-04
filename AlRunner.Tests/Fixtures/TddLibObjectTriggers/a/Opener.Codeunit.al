// #5322: a library that runs objects it does not depend on. It cannot name a report, a query or an xmlport of the
// bundle declared after it, so the call by id is the only thing that connects a test to those triggers (no other form
// KeyGraphProbe knows is in any of the three bundles).
codeunit 73050 "TOT Opener"
{
    procedure RunReportById(ReportId: Integer)
    begin
        Report.Run(ReportId);
    end;

    procedure SaveQueryById(QueryId: Integer)
    var Store: Record "TOT A Store"; O: OutStream;
    begin
        Store.Data.CreateOutStream(O);
        Query.SaveAsCsv(QueryId, O);
    end;

    procedure ExportXmlPortById(XmlPortId: Integer)
    var Store: Record "TOT A Store"; O: OutStream;
    begin
        Store.Data.CreateOutStream(O);
        XmlPort.Export(XmlPortId, O);
    end;

}

table 73051 "TOT A Store"
{
    fields { field(1; PK; Code[20]) { } field(2; Data; Blob) { } }
    keys { key(PK; PK) { Clustered = true; } }
}
