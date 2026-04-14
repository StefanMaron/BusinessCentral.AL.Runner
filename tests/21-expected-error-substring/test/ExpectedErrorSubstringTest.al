codeunit 50921 "Expected Error Substring Tests"
{
    Subtype = Test;

    var
        ErrorProducer: Codeunit "Error Producer";
        Assert: Codeunit Assert;

    [Test]
    procedure TestSubstringMatch()
    begin
        // [WHEN] An error with a long message is thrown
        asserterror ErrorProducer.RaiseCustomerNoError();

        // [THEN] ExpectedError with a substring should pass
        Assert.ExpectedError('must have a value');
    end;

    [Test]
    procedure TestExactMatch()
    begin
        // [WHEN] An error is thrown
        asserterror ErrorProducer.RaiseCustomerNoError();

        // [THEN] ExpectedError with the exact message should pass
        Assert.ExpectedError('The field Customer No. must have a value');
    end;

    [Test]
    procedure TestSubstringMatchMiddle()
    begin
        // [WHEN] An error with a long message is thrown
        asserterror ErrorProducer.RaiseAmountError();

        // [THEN] ExpectedError with a middle substring should pass
        Assert.ExpectedError('must be greater than 0');
    end;

    [Test]
    procedure TestWrongSubstringFails()
    begin
        // [WHEN] An error is thrown
        asserterror ErrorProducer.RaiseCustomerNoError();

        // [THEN] ExpectedError with wrong substring should fail
        asserterror Assert.ExpectedError('completely wrong message');

        // [THEN] The assertion failure itself should be caught
        Assert.ExpectedError('Assert.ExpectedError failed');
    end;

    [Test]
    procedure TestEmptyExpectedErrorPasses()
    begin
        // [WHEN] An error is thrown
        asserterror ErrorProducer.RaiseCustomerNoError();

        // [THEN] ExpectedError with empty string should pass (any error matches)
        Assert.ExpectedError('');
    end;
}
