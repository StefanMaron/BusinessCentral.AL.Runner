codeunit 410004 "NIW Scope Tests"
{
    Subtype = Test;

    [Test]
    procedure PageVarFieldCaption_CompilesWithNoImplicitWith()
    var
        TestRec: Record "NIW Test Record";
        TestPage: TestPage "NIW Test Page";
    begin
        // [GIVEN] A record exists and app.json has NoImplicitWith feature
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Name := 'Test';
        TestRec.Insert();

        // [WHEN] Opening the page — proves FieldCaption page variable compiles
        // without NoImplicitWith this would fail with AL0135 (false positive)
        TestPage.OpenEdit();
        TestPage.Close();

        // [THEN] No compilation or runtime error
        Assert.IsTrue(true, 'Page with FieldCaption page variable compiled and ran with NoImplicitWith');
    end;

    [Test]
    procedure PageVarTableName_CompilesWithNoImplicitWith()
    var
        TestRec: Record "NIW Test Record";
        TestPage: TestPage "NIW Test Page";
    begin
        // [GIVEN] A record exists and app.json has NoImplicitWith feature
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Name := 'Test';
        TestRec.Insert();

        // [WHEN] Opening the page — proves TableName page variable compiles
        // without NoImplicitWith this would fail with AL0166/AL0129 (false positive)
        TestPage.OpenEdit();
        TestPage.Close();

        // [THEN] No compilation or runtime error
        Assert.IsTrue(true, 'Page with TableName page variable compiled and ran with NoImplicitWith');
    end;

    [Test]
    procedure PageVarSameNameAsRecProc_CompilesWithNoImplicitWith()
    var
        TestRec: Record "NIW Test Record";
        TestPage: TestPage "NIW Test Page";
    begin
        // [GIVEN] An active record with a name and app.json has NoImplicitWith
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Name := 'Test';
        TestRec.Active := true;
        TestRec.Insert();

        // [WHEN] Opening the page — proves CanDownloadResult page variable compiles
        // without NoImplicitWith this would fail with AL0129 (false positive)
        TestPage.OpenEdit();
        TestPage.Close();

        // [THEN] No compilation or runtime error
        Assert.IsTrue(true, 'Page with CanDownloadResult page variable compiled and ran with NoImplicitWith');
    end;

    [Test]
    procedure LocalProc_SameNameAsRecMethod_WithNoImplicitWith()
    var
        TestRec: Record "NIW Test Record";
        GetStatusCU: Codeunit "NIW Get Status";
    begin
        // [GIVEN] A record exists and app.json has NoImplicitWith
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Status := '';
        TestRec.Insert();

        // [WHEN] Running the codeunit (OnRun calls GetStatus(Rec))
        GetStatusCU.Run(TestRec);

        // [THEN] Local procedure was called (sets 'FromLocal'), not record method (sets 'FromRecord')
        TestRec.Get(1);
        Assert.AreEqual('FromLocal', TestRec.Status,
            'With NoImplicitWith: GetStatus(Rec) must call local procedure');
    end;

    var
        Assert: Codeunit Assert;
}
