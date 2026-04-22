/// Tests that Report.SaveAs with a RecordRef parameter compiles and runs
/// without throwing in standalone mode (no-op stub contract).
codeunit 98202 "Report SaveAs RecordRef Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Report_SaveAs_WithRecordRef_IsNoOp()
    var
        Src: Codeunit "Report SaveAs RecordRef Src";
    begin
        // [GIVEN] A report id, request data, and a RecordRef
        // [WHEN]  Report.SaveAs(Id, RequestData, Format, OutStream, RecordRef) is called
        // [THEN]  No exception — the 5-arg static overload is a no-op in standalone mode
        Src.SaveAsWithRecordRef(99999, '');
    end;

    [Test]
    procedure Report_SaveAs_WithoutRecordRef_IsNoOp()
    var
        Src: Codeunit "Report SaveAs RecordRef Src";
    begin
        // [GIVEN] A report id and request data
        // [WHEN]  Report.SaveAs(Id, RequestData, Format, OutStream) is called (existing form)
        // [THEN]  No exception — regression guard for existing 4-arg overload
        Src.SaveAsWithoutRecordRef(99999, '');
    end;
}
