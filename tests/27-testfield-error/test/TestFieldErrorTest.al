codeunit 50701 TestFieldErrorTest
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestExpectedTestFieldError_MandatoryFieldEmpty()
    var
        Rec: Record TestFieldTable;
        Helper: Codeunit TestFieldHelper;
    begin
        // [SCENARIO] ExpectedTestFieldError validates that a TestField error was raised.
        // [GIVEN] A record with an empty mandatory field.
        Rec.Init();
        Rec.Code := 'TEST';
        Rec.Insert(false);

        // [WHEN] ValidateRecord is called (which calls TestField).
        asserterror Helper.ValidateRecord(Rec);

        // [THEN] ExpectedTestFieldError should pass since a TestField error was raised.
        Assert.ExpectedTestFieldError(Rec.FieldCaption("Mandatory Field"), '');
    end;

    [Test]
    procedure TestExpectedTestFieldError_NoError_Fails()
    var
        Rec: Record TestFieldTable;
    begin
        // [SCENARIO] ExpectedTestFieldError fails when no error occurred.
        // [GIVEN] No error raised.
        Rec.Init();
        Rec.Code := 'OK';
        Rec."Mandatory Field" := 'Has value';
        Rec.Insert(false);

        // [WHEN] No error occurs (asserterror catches nothing).
        asserterror Rec.TestField("Mandatory Field");

        // [THEN] ExpectedTestFieldError should fail because no TestField error occurred.
        asserterror Assert.ExpectedTestFieldError(Rec.FieldCaption("Mandatory Field"), '');
        Assert.ExpectedError('ExpectedTestFieldError failed');
    end;
}
