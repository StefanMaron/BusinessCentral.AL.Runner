/// Exercises the static Report.* methods (Run, RunModal, Execute, Print,
/// SaveAs*, DefaultLayout, RunRequestPage, GetSubstituteReportId, etc.)
/// so the runner can stub them without a real BC service tier.
codeunit 90000 "RS Src"
{
    procedure CallRun(reportId: Integer)
    begin
        Report.Run(reportId);
    end;

    procedure CallRunModal(reportId: Integer)
    begin
        Report.RunModal(reportId);
    end;

    procedure CallExecute(reportId: Integer)
    begin
        Report.Execute(reportId);
    end;

    procedure CallPrint(reportId: Integer)
    begin
        Report.Print(reportId);
    end;

    procedure CallSaveAsPdf(reportId: Integer; fileName: Text)
    begin
        Report.SaveAsPdf(reportId, fileName);
    end;

    procedure CallSaveAsWord(reportId: Integer; fileName: Text)
    begin
        Report.SaveAsWord(reportId, fileName);
    end;

    procedure CallSaveAsExcel(reportId: Integer; fileName: Text)
    begin
        Report.SaveAsExcel(reportId, fileName);
    end;

    procedure CallSaveAsHtml(reportId: Integer; fileName: Text)
    begin
        Report.SaveAsHtml(reportId, fileName);
    end;

    procedure CallSaveAsXml(reportId: Integer; fileName: Text)
    begin
        Report.SaveAsXml(reportId, fileName);
    end;

    procedure GetDefaultLayout(reportId: Integer): Integer
    begin
        exit(Report.DefaultLayout(reportId));
    end;

    procedure GetSubstituteId(reportId: Integer): Integer
    begin
        exit(Report.GetSubstituteReportId(reportId));
    end;

    procedure GetRunRequestPage(reportId: Integer): Text
    begin
        exit(Report.RunRequestPage(reportId));
    end;
}
