/// Exercises Report instance variable RunRequestPage overloads — issue #1333.
codeunit 308000 "ReportInstanceRRP Src"
{
    /// Call Rep.RunRequestPage(requestParameters) — 1-arg instance overload.
    procedure RunRequestPage1Arg(requestParameters: Text): Text
    var
        Rep: Report "ReportInstanceRRP Report";
    begin
        exit(Rep.RunRequestPage(requestParameters));
    end;

    /// Call Rep.RunRequestPage() — 0-arg instance overload (regression guard).
    procedure RunRequestPage0Arg(): Text
    var
        Rep: Report "ReportInstanceRRP Report";
    begin
        exit(Rep.RunRequestPage());
    end;
}

report 308000 "ReportInstanceRRP Report"
{
    dataset
    {
    }
}
