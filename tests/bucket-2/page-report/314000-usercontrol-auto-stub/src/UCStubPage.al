// Regression test for issue #1588.
// This page's trigger calls CurrPage.MyAddin.DoThing() but has no usercontrol
// declaration for 'MyAddin'. This simulates a dep-extracted page whose usercontrol
// was stripped during dep-extract but whose trigger body was preserved.
// The runner must auto-inject a stub usercontrol + ControlAddin so the page
// compiles without AL0132.
page 1320010 "UCStub Page"
{
    PageType = Card;

    trigger OnOpenPage()
    begin
        // This call requires usercontrol(MyAddin; ...) to be declared on this page.
        // Without the auto-stub injection, this produces AL0132.
        CurrPage.MyAddin.DoThing();
    end;
}
