codeunit 310007 "PVR Scope Tests"
{
    Subtype = Test;

    [Test]
    procedure PageVar_SameNameAsRecProc_True()
    var
        TestRec: Record "PVR Test Record";
        TestPage: TestPage "PVR Test Page";
    begin
        // [GIVEN] An active record with a name (CanDownloadResult() returns true)
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Name := 'Test';
        TestRec.Active := true;
        TestRec.Insert();

        // [WHEN] Opening the page (OnAfterGetCurrRecord assigns CanDownloadResult variable)
        TestPage.OpenEdit();

        // [THEN] CanDownloadResult page variable holds the return value of Rec.CanDownloadResult()
        // (page variable must not be confused with the record procedure of the same name)
        Assert.AreEqual(Format(true), TestPage.CanDownloadField.Value,
            'CanDownloadResult page variable must resolve to variable, not record procedure');

        TestPage.Close();
    end;

    [Test]
    procedure PageVar_SameNameAsRecProc_False()
    var
        TestRec: Record "PVR Test Record";
        TestPage: TestPage "PVR Test Page";
    begin
        // [GIVEN] An inactive record (CanDownloadResult() returns false)
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Name := 'Test';
        TestRec.Active := false;
        TestRec.Insert();

        // [WHEN] Opening the page
        TestPage.OpenEdit();

        // [THEN] CanDownloadResult is false (proving the record procedure was actually called)
        Assert.AreEqual(Format(false), TestPage.CanDownloadField.Value,
            'CanDownloadResult must be false for inactive record');

        TestPage.Close();
    end;

    var
        Assert: Codeunit Assert;
}
