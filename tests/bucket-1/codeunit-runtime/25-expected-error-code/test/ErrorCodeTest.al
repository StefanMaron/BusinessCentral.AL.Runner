codeunit 50501 ErrorCodeTest
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestExpectedErrorCode_DialogError()
    var
        Producer: Codeunit ErrorProducer;
    begin
        // [SCENARIO] ExpectedErrorCode('Dialog') verifies that an Error() was raised.
        // [GIVEN] A codeunit that calls Error().
        // [WHEN] We wrap it in asserterror.
        asserterror Producer.RaiseDialogError();

        // [THEN] ExpectedErrorCode with 'Dialog' passes because an error occurred.
        Assert.ExpectedErrorCode('Dialog');
    end;

    [Test]
    procedure TestExpectedErrorCode_NoError_Fails()
    var
        Producer: Codeunit ErrorProducer;
    begin
        // [SCENARIO] ExpectedErrorCode fails when no error was raised.
        // [GIVEN] A codeunit that does NOT raise an error.
        // [WHEN] We wrap it in asserterror (no error occurs).
        asserterror Producer.NoError();

        // [THEN] ExpectedErrorCode should fail because no error occurred.
        asserterror Assert.ExpectedErrorCode('Dialog');
        Assert.ExpectedError('Assert.ExpectedErrorCode failed');
    end;
}
