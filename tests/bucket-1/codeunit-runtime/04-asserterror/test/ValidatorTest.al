codeunit 50903 "Input Validator Tests"
{
    Subtype = Test;

    var
        Validator: Codeunit "Input Validator";
        Assert: Codeunit Assert;

    [Test]
    procedure TestValidateEmail_Empty()
    begin
        // [WHEN] Validating an empty email
        asserterror Validator.ValidateEmail('');

        // [THEN] Should get "must not be empty" error
        Assert.ExpectedError('Email address must not be empty');
    end;

    [Test]
    procedure TestValidateEmail_NoAtSign()
    begin
        asserterror Validator.ValidateEmail('invalid-email');
        Assert.ExpectedError('Email address must contain @');
    end;

    [Test]
    procedure TestValidateEmail_NoDomain()
    begin
        asserterror Validator.ValidateEmail('user@');
        Assert.ExpectedError('Email address must contain a domain');
    end;

    [Test]
    procedure TestValidateQuantity_Negative()
    begin
        asserterror Validator.ValidateQuantity(-1);
        Assert.ExpectedError('Quantity must not be negative');
    end;

    [Test]
    procedure TestValidateQuantity_TooLarge()
    begin
        asserterror Validator.ValidateQuantity(100000);
        Assert.ExpectedError('Quantity must not exceed 99999');
    end;

    [Test]
    procedure TestValidatePercentage_Negative()
    begin
        asserterror Validator.ValidatePercentage(-5);
        Assert.ExpectedError('Percentage must not be negative');
    end;

    [Test]
    procedure TestValidatePercentage_Over100()
    begin
        asserterror Validator.ValidatePercentage(150);
        Assert.ExpectedError('Percentage must not exceed 100');
    end;
}
