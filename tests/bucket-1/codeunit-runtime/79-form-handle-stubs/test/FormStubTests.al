// Renumbered from 57901 to avoid collision in new bucket layout (#1385).
codeunit 1057901 "Form Stub Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestSetTableViewNoOp()
    var
        Logic: Codeunit "Form Stub Logic";
    begin
        // [GIVEN] A codeunit that calls Page.SetTableView(Rec)
        // [WHEN] We call it
        Logic.ExercisePageStubs();
        // [THEN] No crash — SetTableView is a no-op
        Assert.IsTrue(true, 'SetTableView should not crash');
    end;

    [Test]
    procedure TestLookupModeDefaultFalse()
    var
        Logic: Codeunit "Form Stub Logic";
    begin
        // [GIVEN] A new page variable
        // [WHEN] We read LookupMode
        // [THEN] Default is false
        Assert.AreEqual(false, Logic.GetLookupMode(), 'LookupMode should default to false');
    end;

    [Test]
    procedure TestEditableDefaultTrue()
    var
        Logic: Codeunit "Form Stub Logic";
    begin
        // [GIVEN] A new page variable
        // [WHEN] We read Editable
        // [THEN] Default is true
        Assert.AreEqual(true, Logic.GetEditable(), 'Editable should default to true');
    end;

    [Test]
    procedure TestPageCaptionDefaultEmpty()
    var
        Logic: Codeunit "Form Stub Logic";
    begin
        // [GIVEN] A new page variable
        // [WHEN] We read Caption
        // [THEN] Default is empty string
        Assert.AreEqual('', Logic.GetCaption(), 'Caption should default to empty');
    end;

    [Test]
    procedure TestGetRecordNoOp()
    var
        Logic: Codeunit "Form Stub Logic";
    begin
        // [GIVEN] A codeunit that calls Page.GetRecord(Rec)
        // [WHEN] We call it
        Logic.ExercisePageStubs();
        // [THEN] No crash
        Assert.IsTrue(true, 'GetRecord(Rec) should not crash');
    end;

    [Test]
    procedure TestClearNoOp()
    var
        Logic: Codeunit "Form Stub Logic";
    begin
        // [GIVEN] A codeunit that calls Clear(Page)
        // [WHEN] We call it
        Logic.ExercisePageStubs();
        // [THEN] No crash
        Assert.IsTrue(true, 'Clear(Page) should not crash');
    end;

    [Test]
    procedure TestCustomActionInvoke()
    var
        TestPg: TestPage "Form Stub Page";
    begin
        // [GIVEN] A test page with a custom action
        TestPg.OpenEdit();
        // [WHEN] We invoke the custom action
        TestPg.MyCustomAction.Invoke();
        // [THEN] No crash — custom action is a no-op
        TestPg.Close();
        Assert.IsTrue(true, 'Custom action invoke should not crash');
    end;

    [Test]
    procedure TestExerciseAllStubsTogether()
    var
        Logic: Codeunit "Form Stub Logic";
    begin
        // [GIVEN] A codeunit that exercises ALL page stubs
        // [WHEN] We call it
        Logic.ExercisePageStubs();
        // [THEN] All stubs work without crashing
        Assert.AreEqual(false, Logic.GetLookupMode(), 'LookupMode still false after exercise');
        Assert.AreEqual(true, Logic.GetEditable(), 'Editable still true after exercise');
        Assert.AreEqual('', Logic.GetCaption(), 'Caption still empty after exercise');
    end;
}
