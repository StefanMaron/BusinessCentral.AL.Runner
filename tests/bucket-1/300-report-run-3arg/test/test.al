codeunit 308202 "RR3 Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RR3 Source";

    // ------------------------------------------------------------------
    // Report.Run(ReportId, RequestPage, SystemPrinter) — 3-arg overload
    // Issue #1336: CS7036 'systemPrinter' missing argument
    // ------------------------------------------------------------------

    [Test]
    procedure ReportRun_ThreeArgs_NoError()
    begin
        // [GIVEN] A report ID
        // [WHEN]  Report.Run(id, false, false) is called — 3-arg overload
        // [THEN]  No error — must compile and execute without crashing
        Src.CallRunThreeArgs(308200);
    end;

    [Test]
    procedure ReportRun_ThreeArgs_RequestPageTrue_NoError()
    begin
        // [GIVEN] A report ID
        // [WHEN]  Report.Run(id, true, false) — requestPage=true, still no-op in standalone mode
        // [THEN]  No error
        Src.CallRunThreeArgsRequestPage(308200);
    end;
}
