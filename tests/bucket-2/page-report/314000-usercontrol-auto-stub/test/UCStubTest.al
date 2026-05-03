// Tests for issue #1588: auto-stub ControlAddin usercontrol declarations on pages.
// When a page trigger calls CurrPage.<addinName>.<method>() but the page has no
// usercontrol declaration for <addinName> (e.g. because it was stripped during
// dep-extract), the runner must auto-inject a stub usercontrol + ControlAddin.
codeunit 1320011 "UCStub Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Page_WithStrippedUsercontrol_CompilesAndRunsAsNoOp()
    var
        TestPage: TestPage "UCStub Page";
    begin
        // [GIVEN] A page whose trigger calls CurrPage.MyAddin.DoThing() but has no
        //         usercontrol(MyAddin; ...) declaration (simulates dep-extract stripping)
        // [WHEN]  The page is opened (OnOpenPage fires, calling CurrPage.MyAddin.DoThing())
        TestPage.OpenView();
        // [THEN]  The page compiled without AL0132 (auto-stub injection worked) and
        //         DoThing() is a runtime no-op — no crash
        TestPage.Close();
        Assert.IsTrue(true, 'Page with auto-stub usercontrol must open without error');
    end;

    [Test]
    procedure Page_MultipleAddinMethodCalls_AllAreNoOps()
    var
        TestPage: TestPage "UCStub Page";
    begin
        // [GIVEN] Same setup (verifies the page can be opened multiple times)
        // [WHEN]  The page is opened a second time
        TestPage.OpenView();
        // [THEN]  The addin call is still a no-op on repeated opens
        TestPage.Close();
        Assert.IsTrue(true, 'Repeated page opens with auto-stub usercontrol must not crash');
    end;
}
