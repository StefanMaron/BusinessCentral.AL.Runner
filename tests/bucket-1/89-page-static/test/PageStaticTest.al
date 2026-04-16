/// Tests for static Page.* method stubs.
/// Verifies that AL code using Page.Run, Page.Update, Page.ObjectId, etc. does not error.
codeunit 89101 "PST Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "PST Source";

    // ------------------------------------------------------------------
    // Page.Run — no-op
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
    // Page.RunModal — returns 0 (Action::OK)
    // ------------------------------------------------------------------

    [Test]
    procedure PageRunModal_ReturnsNonNegative()
    var
        Result: Integer;
    begin
        // [WHEN] Page.RunModal is called
        Result := Src.CallPageRunModal(1);
        // [THEN] Returns a valid integer (0 = OK)
        Assert.IsTrue(Result >= 0, 'Page.RunModal must return a non-negative action value');
    end;

    // ------------------------------------------------------------------
    // Page.Activate — no-op
    // ------------------------------------------------------------------

    [Test]
    procedure PageActivate_NoError()
    begin
        Src.CallPageActivate();
        Assert.IsTrue(true, 'Page.Activate must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page.SaveRecord — no-op
    // ------------------------------------------------------------------

    [Test]
    procedure PageSaveRecord_NoError()
    begin
        Src.CallPageSaveRecord();
        Assert.IsTrue(true, 'Page.SaveRecord must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page.Update — no-op
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
    // Page.SetTableView — no-op
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
    // Page.SetSelectionFilter — no-op
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
    // Page.SetRecord — no-op
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
    // Page.ObjectId — does not crash
    // ------------------------------------------------------------------

    [Test]
    procedure PageObjectId_NoError()
    begin
        // [WHEN] ObjectId(false) is called
        // [THEN] No error is raised
        Src.CallPageObjectId();
        Assert.IsTrue(true, 'Page.ObjectId must not raise an error');
    end;

    // ------------------------------------------------------------------
    // Page.LookupMode — defaults to false
    // ------------------------------------------------------------------

    [Test]
    procedure PageLookupMode_DefaultFalse()
    var
        Mode: Boolean;
    begin
        // [WHEN] LookupMode is read
        Mode := Src.GetPageLookupMode();
        // [THEN] Returns false (default)
        Assert.IsFalse(Mode, 'Page.LookupMode must default to false');
    end;

    // ------------------------------------------------------------------
    // Page.CancelBackgroundTask — no-op
    // ------------------------------------------------------------------

    [Test]
    procedure PageCancelBackgroundTask_NoError()
    begin
        Src.CallCancelBackgroundTask(0);
        Assert.IsTrue(true, 'Page.CancelBackgroundTask must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page.SetBackgroundTaskResult — no-op
    // ------------------------------------------------------------------

    [Test]
    procedure PageSetBackgroundTaskResult_NoError()
    var
        Result: Dictionary of [Text, Text];
    begin
        Src.CallSetBackgroundTaskResult(Result);
        Assert.IsTrue(true, 'Page.SetBackgroundTaskResult must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page.GetBackgroundParameters — no-op
    // ------------------------------------------------------------------

    [Test]
    procedure PageGetBackgroundParameters_NoError()
    var
        Params: Dictionary of [Text, Text];
    begin
        Src.CallGetBackgroundParameters(Params);
        Assert.IsTrue(true, 'Page.GetBackgroundParameters must be a no-op');
    end;

    // ------------------------------------------------------------------
    // Page.EnqueueBackgroundTask — no-op
    // ------------------------------------------------------------------

    [Test]
    procedure PageEnqueueBackgroundTask_NoError()
    var
        TaskId: Integer;
    begin
        Src.CallEnqueueBackgroundTask(TaskId, 1);
        Assert.IsTrue(true, 'Page.EnqueueBackgroundTask must be a no-op');
    end;
}
