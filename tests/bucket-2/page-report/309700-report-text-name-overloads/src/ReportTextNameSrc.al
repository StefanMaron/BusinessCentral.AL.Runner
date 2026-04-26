/// Exercises the Text-name overloads of Report.Execute, Report.Run, Report.RunModal
/// and the 2-argument Report.RunRequestPage(Integer, Text) — issue #1377.
codeunit 309700 "RptTextName Src"
{
    procedure CallExecuteTextName(reportName: Text; requestPageXml: Text; var recRef: RecordRef)
    begin
        Report.Execute(reportName, requestPageXml, recRef);
    end;

    procedure CallRunTextName(reportName: Text; printDialog: Boolean; useRequestPage: Boolean; var rec: Record "RptTextName Table")
    begin
        Report.Run(reportName, printDialog, useRequestPage, rec);
    end;

    procedure CallRunModalTextName(reportName: Text; printDialog: Boolean; useRequestPage: Boolean; var rec: Record "RptTextName Table")
    begin
        Report.RunModal(reportName, printDialog, useRequestPage, rec);
    end;

    procedure CallRunRequestPage2Arg(reportId: Integer; requestParameters: Text): Text
    begin
        exit(Report.RunRequestPage(reportId, requestParameters));
    end;
}
