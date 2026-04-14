codeunit 70501 "Report Skip Helper"
{
    procedure RunReportSkip()
    var
        ReportWithSkip: Report "Report With Skip";
    begin
        // Run the report — this exercises CurrReport.Skip() at runtime
        ReportWithSkip.Run();
    end;
}
