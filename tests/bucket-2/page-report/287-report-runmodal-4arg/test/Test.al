codeunit 163002 "RRM4 Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RRM4 Src";

    // ------------------------------------------------------------------
    // Report.RunModal — all static overloads are no-ops in standalone mode
    // ------------------------------------------------------------------

    [Test]
    procedure Report_RunModal_1Arg_IsNoOp()
    begin
        // [GIVEN] A non-existent report id
        // [WHEN]  Report.RunModal(id) 1-arg is called
        // [THEN]  No error — stub is a no-op in standalone mode
        Src.CallRunModal1Arg(99999);
    end;

    [Test]
    procedure Report_RunModal_2Arg_IsNoOp()
    begin
        // [GIVEN] A non-existent report id
        // [WHEN]  Report.RunModal(id, false) 2-arg is called
        // [THEN]  No error — stub is a no-op in standalone mode
        Src.CallRunModal2Arg(99999, false);
    end;

    [Test]
    procedure Report_RunModal_3Arg_IsNoOp()
    begin
        // [GIVEN] A non-existent report id
        // [WHEN]  Report.RunModal(id, false, false) 3-arg is called
        // [THEN]  No error — stub is a no-op in standalone mode
        Src.CallRunModal3Arg(99999, false, false);
    end;

    [Test]
    procedure Report_RunModal_4Arg_IsNoOp()
    var
        DummyRec: Record "RRM4 Dummy";
    begin
        // [GIVEN] A non-existent report id and a record variable
        // [WHEN]  Report.RunModal(id, false, false, Rec) 4-arg is called
        // [THEN]  No error — stub is a no-op in standalone mode
        Src.CallRunModal4Arg(99999, false, false, DummyRec);
    end;
}
