/// Tests for Page.* method stubs — static (Run, RunModal) and instance (Update, ObjectId, etc.)
codeunit 89101 "PST Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "PST Source";

    // ------------------------------------------------------------------
    // Page.Run — static no-op
    // ------------------------------------------------------------------

    [Test]
    procedure PageRun_NoError()
    begin
        // [WHEN] Page.Run is called with any page id
        // [THEN] No error is raised
        Src.CallPageRun(1);
        Assert.IsTrue(true, 'Page.Run must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page.RunModal — static, returns Action::None
    // ------------------------------------------------------------------

    [Test]
    procedure PageRunModal_ReturnsAction()
    var
        Result: Action;
    begin
        // [WHEN] Page.RunModal is called
        Result := Src.CallPageRunModal(1);
        // [THEN] Returns Action::None (stub default = 0)
        Assert.AreEqual(Action::None, Result, 'Page.RunModal must return Action::None from stub');
    end;

    // ------------------------------------------------------------------
    // Page variable — Activate
    // ------------------------------------------------------------------

    [Test]
    procedure PageActivate_NoError()
    begin
        Src.CallPageActivate();
        Assert.IsTrue(true, 'Page.Activate must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page variable — SaveRecord
    // ------------------------------------------------------------------

    [Test]
    procedure PageSaveRecord_NoError()
    begin
        Src.CallPageSaveRecord();
        Assert.IsTrue(true, 'Page.SaveRecord must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page variable — Update
    // ------------------------------------------------------------------

    [Test]
    procedure PageUpdate_NoError()
    begin
        Src.CallPageUpdate();
        Assert.IsTrue(true, 'Page.Update() must be a no-op');
    end;

    [Test]
    procedure PageUpdateBool_NoError()
    begin
        Src.CallPageUpdateBool(true);
        Src.CallPageUpdateBool(false);
        Assert.IsTrue(true, 'Page.Update(bool) must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page variable — SetTableView
    // ------------------------------------------------------------------

    [Test]
    procedure PageSetTableView_NoError()
    var
        Rec: Record "PST Record";
    begin
        Src.CallPageSetTableView(Rec);
        Assert.IsTrue(true, 'Page.SetTableView must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page variable — SetSelectionFilter
    // ------------------------------------------------------------------

    [Test]
    procedure PageSetSelectionFilter_NoError()
    var
        Rec: Record "PST Record";
    begin
        Src.CallPageSetSelectionFilter(Rec);
        Assert.IsTrue(true, 'Page.SetSelectionFilter must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page variable — SetRecord
    // ------------------------------------------------------------------

    [Test]
    procedure PageSetRecord_NoError()
    var
        Rec: Record "PST Record";
    begin
        Src.CallPageSetRecord(Rec);
        Assert.IsTrue(true, 'Page.SetRecord must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page variable — ObjectId
    // ------------------------------------------------------------------

    [Test]
    procedure PageObjectId_ReturnsText()
    var
        T: Text;
    begin
        // [WHEN] ObjectId(false) is called on a page variable
        T := Src.GetPageObjectId();
        // [THEN] Returns without error (stub returns empty text)
        Assert.IsTrue(true, 'Page.ObjectId must not raise an error');
    end;

    // ------------------------------------------------------------------
    // Page variable — LookupMode get/set
    // ------------------------------------------------------------------

    [Test]
    procedure PageLookupMode_DefaultFalse()
    var
        Mode: Boolean;
    begin
        // [WHEN] LookupMode is read without prior assignment
        Mode := Src.GetPageLookupMode();
        // [THEN] Returns false (default)
        Assert.IsFalse(Mode, 'Page.LookupMode must default to false');
    end;

    [Test]
    procedure PageLookupMode_SetAndGet()
    var
        Mode: Boolean;
    begin
        // [WHEN] LookupMode is set to true then read back
        Mode := Src.SetAndGetPageLookupMode(true);
        // [THEN] Returns true (value was stored)
        Assert.IsTrue(Mode, 'Page.LookupMode must return the value that was set');
    end;
}
