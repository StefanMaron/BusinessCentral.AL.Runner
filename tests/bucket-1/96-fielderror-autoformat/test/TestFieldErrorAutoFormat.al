codeunit 1264002 "Test FieldError AutoFormat"
{
    Subtype = Test;

    [Test]
    procedure FieldError_InTrigger_RaisesError()
    var
        Rec: Record "Field Error AutoFormat";
    begin
        // [GIVEN] A record with Code but no Description
        Rec.Code := 'TEST';
        // Description is blank

        // [WHEN] Insert fires the OnInsert trigger
        // [THEN] FieldError raises an error containing the field caption
        asserterror Rec.Insert(true);
        Assert.ExpectedError('Description');
    end;

    [Test]
    procedure FieldError_InTrigger_DefaultMessage()
    var
        Rec: Record "Field Error AutoFormat";
    begin
        // [GIVEN] A record with Code but no Description
        Rec.Code := 'TEST2';

        // [WHEN] Insert fires the OnInsert trigger
        // [THEN] FieldError raises an error with default "must have a value"
        asserterror Rec.Insert(true);
        Assert.ExpectedError('must have a value');
    end;

    [Test]
    procedure FieldError_InTableProc_CustomMessage()
    var
        Rec: Record "Field Error AutoFormat";
    begin
        // [GIVEN] A record with blank Code
        // Code is blank by default

        // [WHEN] ValidateCode is called
        // [THEN] FieldError raises an error with custom message
        asserterror Rec.ValidateCode();
        Assert.ExpectedError('must not be blank');
    end;

    [Test]
    procedure FieldError_InTableProc_ContainsFieldCaption()
    var
        Rec: Record "Field Error AutoFormat";
    begin
        // [GIVEN] A record with blank Code

        // [WHEN] ValidateCode is called
        // [THEN] Error contains the field caption "Code"
        asserterror Rec.ValidateCode();
        Assert.ExpectedError('Code');
    end;

    [Test]
    procedure FieldError_SuccessPath_NoError()
    var
        Rec: Record "Field Error AutoFormat";
    begin
        // [GIVEN] A record with all required fields filled
        Rec.Code := 'VALID';
        Rec.Description := 'Valid description';

        // [WHEN] Insert is called with valid data
        Rec.Insert(true);

        // [THEN] No error is raised, record is inserted
        Assert.IsTrue(Rec.Get('VALID'), 'Record should exist after insert');
    end;

    var
        Assert: Codeunit "Library Assert";
}
