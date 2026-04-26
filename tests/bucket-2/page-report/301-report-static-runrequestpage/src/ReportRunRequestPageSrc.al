/// Exercises Report.RunRequestPage with the 2-argument overload (reportId, requestParameters).
codeunit 307400 "RRP Src"
{
    /// Call Report.RunRequestPage(reportId, requestParameters) — 2-arg overload.
    procedure RunRequestPage2Arg(reportId: Integer; requestParameters: Text): Text
    begin
        exit(Report.RunRequestPage(reportId, requestParameters));
    end;

    /// Call Report.RunRequestPage(reportId) — 1-arg overload (regression guard).
    procedure RunRequestPage1Arg(reportId: Integer): Text
    begin
        exit(Report.RunRequestPage(reportId));
    end;
}
