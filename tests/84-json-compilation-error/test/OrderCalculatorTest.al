codeunit 50842 "Order Calculator Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TotalWithTax_TenPercent()
    var
        Calc: Codeunit "Order Calculator";
        Result: Decimal;
    begin
        // Positive: correct calculation passes
        Result := Calc.TotalWithTax(100, 10);
        Assert.AreEqual(110, Result, 'Expected 110 for 100 + 10%');
    end;

    [Test]
    procedure TotalWithTax_ZeroAmount()
    var
        Calc: Codeunit "Order Calculator";
        Result: Decimal;
    begin
        // Positive: zero amount stays zero
        Result := Calc.TotalWithTax(0, 25);
        Assert.AreEqual(0, Result, 'Expected 0 for zero amount');
    end;

    [Test]
    procedure TotalWithTax_WrongResult_Fails()
    var
        Calc: Codeunit "Order Calculator";
        Result: Decimal;
    begin
        // Negative: assert that incorrect expected value fails (proves runner catches failures)
        Result := Calc.TotalWithTax(100, 10);
        asserterror Assert.AreEqual(999, Result, 'Should fail - wrong expected value');
    end;
}
