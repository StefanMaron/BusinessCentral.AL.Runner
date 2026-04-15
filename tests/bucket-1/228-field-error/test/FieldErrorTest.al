codeunit 60101 FieldErrorTest
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure FieldError_WithMessage_RaisesError()
    var
        Rec: Record FieldErrorTable;
        Helper: Codeunit FieldErrorHelper;
    begin
        // [GIVEN] A record with a populated key
        Rec.Init();
        Rec.Code := 'TEST';
        Rec.Insert(false);

        // [WHEN] FieldError is called with a custom message
        asserterror Helper.RaiseFieldErrorWithMessage(Rec);

        // [THEN] The error text contains the field caption and the custom message
        Assert.ExpectedError('must not be empty');
    end;

    [Test]
    procedure FieldError_NoMessage_RaisesError()
    var
        Rec: Record FieldErrorTable;
        Helper: Codeunit FieldErrorHelper;
    begin
        // [GIVEN] A record with a populated key
        Rec.Init();
        Rec.Code := 'TEST2';
        Rec.Insert(false);

        // [WHEN] FieldError is called without a message
        asserterror Helper.RaiseFieldErrorNoMessage(Rec);

        // [THEN] The error text references the field name
        Assert.ExpectedError('Name');
    end;

    [Test]
    procedure FieldError_NoErrorRaised_AssertFails()
    var
        Rec: Record FieldErrorTable;
    begin
        // [GIVEN] A record with a populated key
        Rec.Init();
        Rec.Code := 'OK';
        Rec."Name" := 'Something';
        Rec.Insert(false);

        // [WHEN] No FieldError call is made
        // [THEN] ExpectedError should fail since no error occurred
        asserterror Assert.ExpectedError('must not be empty');
    end;
}
