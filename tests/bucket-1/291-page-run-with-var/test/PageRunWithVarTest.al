/// Tests for Page.Run/RunModal with page variables and the implicit NavForm conversion.
/// Issue #1106: CS1503 — Page<N> can't be passed where NavForm is expected after
/// NavForm is stripped from the page class base list.  The fix injects an implicit
/// conversion operator on every generated Page<N> class.
codeunit 29101 "PRV Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "PRV Source";

    [Test]
    procedure PageRunWithRecord_IsNoOp()
    var
        Rec: Record "PRV Row";
    begin
        // [WHEN] Page.Run(PageId, Rec) is called (BC lowers to NavForm.Run)
        // [THEN] No error — the call compiles and is a no-op
        Src.RunWithRecord(Rec);
        Assert.IsTrue(true, 'Page.Run(PageId, Rec) must compile and be a no-op');
    end;

    [Test]
    procedure PageRunModalWithRecord_ReturnsActionNone()
    var
        Rec: Record "PRV Row";
        Result: Action;
    begin
        // [WHEN] Page.RunModal(PageId, Rec) is called
        Result := Src.RunModalWithRecord(Rec);
        // [THEN] Returns Action::None (stub default)
        Assert.AreEqual(Action::None, Result, 'Page.RunModal(PageId, Rec) must return Action::None');
    end;

    [Test]
    procedure PageVarRun_IsNoOp()
    begin
        // [WHEN] A page variable's .Run() is called
        // [THEN] No error — MockFormHandle.Run() is a no-op
        Src.PageVarRun();
        Assert.IsTrue(true, 'Page var .Run() must be a no-op');
    end;

    [Test]
    procedure PageVarSetRecord_IsNoOp()
    var
        Rec: Record "PRV Row";
    begin
        // [WHEN] SetRecord and Run are called on a page variable
        // [THEN] No error
        Src.PageVarSetRecord(Rec);
        Assert.IsTrue(true, 'Page var SetRecord+Run must compile and be no-ops');
    end;

    [Test]
    [HandlerFunctions('PageVarRunModalHandler')]
    procedure PageVarRunModal_ReturnsActionNone()
    var
        Result: Action;
    begin
        // [WHEN] RunModal() is called on a page variable (with ModalPageHandler)
        Result := Src.PageVarRunModal();
        // [THEN] Returns the action set by the handler (Action::LookupOK when TestPage.OK is invoked)
        Assert.AreEqual(Action::LookupOK, Result, 'Page var .RunModal() must return handler action');
    end;

    [ModalPageHandler]
    procedure PageVarRunModalHandler(var Page: TestPage "PRV Card")
    begin
        Page.OK().Invoke();
    end;
}
