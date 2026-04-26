codeunit 29802 "RR4 Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "RR4 Source";

    // ------------------------------------------------------------------
    // Report.Run(ReportId, RequestPage, SystemPrinter, Record) — 4-arg
    // ------------------------------------------------------------------

    [Test]
    procedure ReportRun_FourArgs_NoError()
    var
        Rec: Record "RR4 Table";
    begin
        // [GIVEN] A record variable (empty, no filter)
        // [WHEN]  Report.Run(id, false, false, Rec) is called
        // [THEN]  No error — 4-arg overload must compile and execute without crashing
        Src.CallRunFourArgs(29800, Rec);
    end;

    [Test]
    procedure ReportRun_FourArgs_FilterSurvivesCall()
    var
        FilterAfter: Text;
    begin
        // [GIVEN] A record with a filter applied
        // [WHEN]  Report.Run(id, false, false, Rec) is called
        // [THEN]  The filter on the record variable is still present after the call
        FilterAfter := Src.SetupTableAndRun(29800);
        Assert.AreEqual('R4-001', FilterAfter, 'Filter on record variable must survive Report.Run call');
    end;
}
