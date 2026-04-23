/// Report that calls CurrReport.ObjectId(false) in OnPreReport (issue #1191).
report 230001 "ROI Report"
{
    UsageCategory = None;

    dataset
    {
    }

    trigger OnPreReport()
    begin
        // BC emits CurrReport.ObjectId(false) in report triggers.
        // After NavReport base class is stripped, ObjectId must be available as an instance method.
        ReportObjectId := CurrReport.ObjectId(false);
    end;

    var
        ReportObjectId: Text;

    procedure GetObjectId(): Text
    begin
        exit(ReportObjectId);
    end;
}

/// Helper codeunit — runs the report and retrieves the captured ObjectId.
codeunit 230002 "ROI Helper"
{
    procedure RunAndGetObjectId(): Text
    var
        Rep: Report "ROI Report";
    begin
        Rep.Run();
        exit(Rep.GetObjectId());
    end;

    procedure ObjectIdNotEmpty(): Boolean
    var
        Rep: Report "ROI Report";
    begin
        Rep.Run();
        exit(Rep.GetObjectId() <> '');
    end;
}
