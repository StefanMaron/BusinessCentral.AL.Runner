codeunit 310202 "RR2 Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RR2 Source";

    // ------------------------------------------------------------------
    // Report.Run(ReportId, RequestPage) — 2-arg overload
    // Issue #1427: CS1501 'StaticRun' (2 args) no overload
    // ------------------------------------------------------------------

    [Test]
    procedure ReportRun_TwoArgs_RequestPageFalse_NoError()
    begin
        // [GIVEN] A report ID
        // [WHEN]  Report.Run(id, false) is called — 2-arg overload
        // [THEN]  No error — must compile and execute without crashing
        Src.CallRunTwoArgs(310200);
    end;

    [Test]
    procedure ReportRun_TwoArgs_RequestPageTrue_NoError()
    begin
        // [GIVEN] A report ID
        // [WHEN]  Report.Run(id, true) — requestPage=true, still no-op in standalone mode
        // [THEN]  No error
        Src.CallRunTwoArgsRequestPageTrue(310200);
    end;
}
