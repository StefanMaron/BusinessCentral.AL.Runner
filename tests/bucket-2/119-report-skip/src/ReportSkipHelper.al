codeunit 70501 "Report Skip Helper"
{
    procedure RunReportSkip(): Boolean
    begin
        // Just verify the report compiles — CurrReport.Skip() must be available
        exit(true);
    end;
}
