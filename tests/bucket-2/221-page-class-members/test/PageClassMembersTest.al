/// Tests for Page<N>.RunModal() and LookupMode members injected on generated page classes.
/// Issue #1079: CS1061 on 'Page<N>': missing 'LookupMode', 'RunModal'.
///
/// When AL code inside a page trigger uses CurrPage.LookupMode or CurrPage.RunModal(),
/// BC generates calls on the Page<N> class directly (via CurrPage => (Page<N>)this).
/// Without injection, Roslyn compilation fails with CS1061.
codeunit 60491 "PCM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "PCM Src";

    [Test]
    procedure PageWithCurrPageLookupMode_Compiles()
    begin
        // [GIVEN] A page whose OnOpenPage trigger sets CurrPage.LookupMode := true
        // [WHEN]  The test suite compiles and runs (LookupMode injected on Page<N>)
        // [THEN]  No CS1061 error — compilation succeeds and this test runs
        Assert.IsTrue(Src.LookupModePageCompiles(),
            'Page<N> must have LookupMode injected so CurrPage.LookupMode compiles (issue #1079)');
    end;

    [Test]
    procedure PageWithCurrPageRunModal_Compiles()
    begin
        // [GIVEN] A page whose OnOpenPage trigger calls CurrPage.RunModal()
        // [WHEN]  The test suite compiles and runs (RunModal() injected on Page<N>)
        // [THEN]  No CS1061 error — compilation succeeds and this test runs
        Assert.IsTrue(Src.RunModalPageCompiles(),
            'Page<N> must have RunModal() injected so CurrPage.RunModal() compiles (issue #1079)');
    end;
}
