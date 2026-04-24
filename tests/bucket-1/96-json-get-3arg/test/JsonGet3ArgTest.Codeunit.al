codeunit 1281002 "JSON Get 3-Arg Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Helper: Codeunit "JSON Get 3-Arg Helper";

    [Test]
    procedure GetInteger_3Arg_ReturnsValue()
    var
        Result: Integer;
    begin
        // [GIVEN] A JSON object with an integer property
        // [WHEN] GetInteger is called with requireValueExists = true
        Result := Helper.GetIntegerFromJson('{"qty": 42}', 'qty', true);

        // [THEN] The correct integer value is returned
        Assert.AreEqual(42, Result, 'GetInteger with 3 args should return the integer value');
    end;

    [Test]
    procedure GetInteger_3Arg_MissingKey_NoRequire_ReturnsDefault()
    var
        Result: Integer;
    begin
        // [GIVEN] A JSON object without the requested key
        // [WHEN] GetInteger is called with requireValueExists = false
        Result := Helper.GetIntegerFromJson('{"other": 1}', 'qty', false);

        // [THEN] Default integer (0) is returned
        Assert.AreEqual(0, Result, 'GetInteger with missing key and requireValueExists=false should return 0');
    end;

    [Test]
    procedure GetInteger_3Arg_MissingKey_Require_Throws()
    begin
        // [GIVEN] A JSON object without the requested key
        // [WHEN] GetInteger is called with requireValueExists = true
        asserterror Helper.GetIntegerFromJson('{"other": 1}', 'qty', true);

        // [THEN] An error is thrown
        Assert.ExpectedError('does not contain a property');
    end;

    [Test]
    procedure GetBoolean_3Arg_ReturnsValue()
    var
        Result: Boolean;
    begin
        // [GIVEN] A JSON object with a boolean property
        // [WHEN] GetBoolean is called with requireValueExists = true
        Result := Helper.GetBooleanFromJson('{"active": true}', 'active', true);

        // [THEN] The correct boolean value is returned
        Assert.IsTrue(Result, 'GetBoolean with 3 args should return true');
    end;

    [Test]
    procedure GetBoolean_3Arg_MissingKey_NoRequire_ReturnsDefault()
    var
        Result: Boolean;
    begin
        // [GIVEN] A JSON object without the requested key
        // [WHEN] GetBoolean is called with requireValueExists = false
        Result := Helper.GetBooleanFromJson('{"other": 1}', 'active', false);

        // [THEN] Default boolean (false) is returned
        Assert.IsFalse(Result, 'GetBoolean with missing key and requireValueExists=false should return false');
    end;

    [Test]
    procedure GetBoolean_3Arg_MissingKey_Require_Throws()
    begin
        // [GIVEN] A JSON object without the requested key
        // [WHEN] GetBoolean is called with requireValueExists = true
        asserterror Helper.GetBooleanFromJson('{"other": 1}', 'active', true);

        // [THEN] An error is thrown
        Assert.ExpectedError('does not contain a property');
    end;

    [Test]
    procedure GetDecimal_3Arg_ReturnsValue()
    var
        Result: Decimal;
    begin
        // [GIVEN] A JSON object with a decimal property
        // [WHEN] GetDecimal is called with requireValueExists = true
        Result := Helper.GetDecimalFromJson('{"price": 19.99}', 'price', true);

        // [THEN] The correct decimal value is returned
        Assert.AreEqual(19.99, Result, 'GetDecimal with 3 args should return the decimal value');
    end;

    [Test]
    procedure GetDecimal_3Arg_MissingKey_NoRequire_ReturnsDefault()
    var
        Result: Decimal;
    begin
        // [GIVEN] A JSON object without the requested key
        // [WHEN] GetDecimal is called with requireValueExists = false
        Result := Helper.GetDecimalFromJson('{"other": 1}', 'price', false);

        // [THEN] Default decimal (0) is returned
        Assert.AreEqual(0, Result, 'GetDecimal with missing key and requireValueExists=false should return 0');
    end;

    [Test]
    procedure GetDecimal_3Arg_MissingKey_Require_Throws()
    begin
        // [GIVEN] A JSON object without the requested key
        // [WHEN] GetDecimal is called with requireValueExists = true
        asserterror Helper.GetDecimalFromJson('{"other": 1}', 'price', true);

        // [THEN] An error is thrown
        Assert.ExpectedError('does not contain a property');
    end;
}
