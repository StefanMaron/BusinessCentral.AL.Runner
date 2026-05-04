// Tests for issue #1597: page parts must not trigger stub usercontrol injection.
// When a page has part(X; SomePage) and calls CurrPage.X.Page.Method(), the runner
// must not inject a stub usercontrol for X — it is already declared as a PagePart.
codeunit 1320102 "Ppcs Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure HostPage_WithPartAndCurrPageCall_CompilesWithoutAL0155_IsNoOp()
    var
        TestPage: TestPage "Ppcs Host Page";
    begin
        // [GIVEN] A page with part(MySubPage; "Ppcs Sub Page") that calls
        //         CurrPage.MySubPage.Page.DoSomething() in OnOpenPage
        // [WHEN]  The page is opened (triggers auto-stub injection guard)
        TestPage.OpenView();
        // [THEN]  No AL0155 (no duplicate member) and no AL0132 (no wrong return type).
        //         DoSomething() is a no-op via the runtime part-page stub.
        TestPage.Close();
        Assert.IsTrue(true, 'Page with part must open without AL0155 or AL0132');
    end;
}
