codeunit 294002 "Same Arity Dispatch Test"
{
    Subtype = Test;

    var
        Helper: Codeunit "Multi Return Helper";
        Assert: Codeunit "Library Assert";

    [Test]
    procedure GetCode_ReturnsCorrctType()
    var
        Result: Code[20];
    begin
        // [GIVEN] A codeunit with 5 methods, all taking 1 Integer arg but returning different types
        // [WHEN] Call GetCode which returns Code[20]
        Result := Helper.GetCode(42);
        // [THEN] Correct method dispatched, returns formatted integer as Code
        Assert.AreEqual('42', Result, 'GetCode should return the integer formatted as Code');
    end;

    [Test]
    procedure GetInteger_ReturnsCorrectValue()
    var
        Result: Integer;
    begin
        Result := Helper.GetInteger(7);
        Assert.AreEqual(21, Result, 'GetInteger should return input * 3');
    end;

    [Test]
    procedure GetText_ReturnsCorrectValue()
    var
        Result: Text;
    begin
        Result := Helper.GetText(5);
        Assert.AreEqual('T5', Result, 'GetText should return T prefix + formatted input');
    end;

    [Test]
    procedure GetBoolean_ReturnsCorrectValue()
    var
        Result: Boolean;
    begin
        Result := Helper.GetBoolean(1);
        Assert.IsTrue(Result, 'GetBoolean(1) should return true');

        Result := Helper.GetBoolean(-1);
        Assert.IsFalse(Result, 'GetBoolean(-1) should return false');
    end;

    [Test]
    procedure GetDecimal_ReturnsCorrectValue()
    var
        Result: Decimal;
    begin
        Result := Helper.GetDecimal(10);
        Assert.AreEqual(5.0, Result, 'GetDecimal should return input / 2');
    end;
}
