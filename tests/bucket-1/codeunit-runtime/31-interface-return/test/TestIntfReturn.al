codeunit 53100 "Test Interface Return"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure InterfaceReturnedFromFunction()
    var
        Factory: Codeunit "Calc Factory";
        Calc: Interface "ICalc";
        Result: Decimal;
    begin
        // Positive: interface can be returned from a function and used
        Calc := Factory.GetCalculator();
        Result := Calc.Calculate(10, 20);
        Assert.AreEqual(30, Result, 'Should calculate 10 + 20 = 30');
    end;

    [Test]
    procedure InterfaceReturnedGivesWrongResult()
    var
        Factory: Codeunit "Calc Factory";
        Calc: Interface "ICalc";
        Result: Decimal;
    begin
        // Negative: verify the interface actually computes, not just returns default
        Calc := Factory.GetCalculator();
        Result := Calc.Calculate(7, 3);
        Assert.AreNotEqual(0, Result, 'Result should not be zero');
        Assert.AreNotEqual(7, Result, 'Result should not be just the first arg');
    end;
}
