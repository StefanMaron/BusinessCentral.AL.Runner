codeunit 50480 "PV Page Var Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageVariableRunIsNoOp()
    var
        P: Page "PV Probe Page";
    begin
        // [GIVEN] A Page "X" variable — BC emits new NavFormHandle(this, pageId),
        //         which must rewrite to MockFormHandle so the scope class compiles.
        // [WHEN] Calling .Run() on it
        // [THEN] Execution reaches the assertion without touching UI
        P.Run();
        Assert.IsTrue(true, 'Page variable .Run() must compile and no-op');
    end;

    [Test]
    procedure PageRunStaticFormWithRecord()
    var
        R: Record "PV Row";
    begin
        // Covers the #6 scenario too: static Page.Run(Page::X, Rec) inside a
        // test procedure must compile and execute as a no-op.
        Page.Run(Page::"PV Probe Page", R);
        Assert.IsTrue(true, 'Page.Run(Page::X, Rec) must compile and no-op');
    end;
}
