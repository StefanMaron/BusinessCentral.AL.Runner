codeunit 310004 "PVB Scope Tests"
{
    Subtype = Test;

    [Test]
    procedure PageVarFieldCaption_AssignAndRead()
    var
        TestRec: Record "PVB Test Record";
        TestPage: TestPage "PVB Test Page";
    begin
        // [GIVEN] A record exists
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Name := 'Test';
        TestRec.Insert();

        // [WHEN] Opening the page (OnAfterGetRecord assigns FieldCaption page variable)
        TestPage.OpenEdit();

        // [THEN] FieldCaption page variable was assigned (not confused with built-in FieldCaption() method)
        Assert.AreEqual('Custom Caption', TestPage.FieldCaptionField.Value,
            'FieldCaption page variable must not resolve to built-in FieldCaption() method');

        TestPage.Close();
    end;

    [Test]
    procedure PageVarTableName_AssignAndRead()
    var
        TestRec: Record "PVB Test Record";
        TestPage: TestPage "PVB Test Page";
    begin
        // [GIVEN] A record exists
        TestRec.DeleteAll();
        TestRec.Id := 1;
        TestRec.Name := 'Test';
        TestRec.Insert();

        // [WHEN] Opening the page (OnAfterGetRecord assigns TableName page variable)
        TestPage.OpenEdit();

        // [THEN] TableName page variable was assigned (not confused with built-in TableName property)
        Assert.AreEqual('Custom Table', TestPage.TableNameField.Value,
            'TableName page variable must not resolve to built-in Record.TableName property');

        TestPage.Close();
    end;

    var
        Assert: Codeunit Assert;
}
