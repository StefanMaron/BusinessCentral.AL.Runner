/// Tests that CurrPage.Run() compiles and is a no-op when called on a Page<N> class.
/// Issue #1444: CS1061 — 'Page70327080' does not contain a definition for 'Run'.
/// BC lowers CurrPage.Run() to this.Run() on the emitted Page class; the runner must
/// inject a Run() stub into every Page<N> (no-op, consistent with the headless contract).
codeunit 312001 "CPR Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CurrPageRun_CompilationSucceeds_NoThrow()
    var
        Src: Codeunit "CPR Source";
        Result: Integer;
    begin
        // [GIVEN] A page that calls CurrPage.Run() in an action trigger
        // [WHEN]  The suite compiles (i.e. the test method runs at all)
        // [THEN]  No CS1061 compilation error — the page compiled successfully

        // Prove execution reached this line (not just "test ran without throwing").
        Result := Src.RunCard();
        Assert.AreEqual(42, Result, 'CurrPage.Run() page must compile and RunCard must return 42');
    end;
}
