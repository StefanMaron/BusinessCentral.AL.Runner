/// Exercises the static Report.* methods that are available across BC 26-28.
codeunit 91000 "RS Src"
{
    procedure CallRun(reportId: Integer)
    begin
        Report.Run(reportId);
    end;

    procedure CallRunModal(reportId: Integer)
    begin
        Report.RunModal(reportId);
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

    procedure CallSaveAsXml(reportId: Integer; fileName: Text)
    begin
        Report.SaveAsXml(reportId, fileName);
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
