codeunit 51014 "MS Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure BothSubscribersFireOnSameEvent()
    var
        Publisher: Codeunit "MS Publisher";
        Result: Integer;
    begin
        // Positive: both subscriber A (+10) and subscriber B (+20)
        // fire on the same event, so result = 5 + 10 + 20 = 35
        Result := Publisher.DoCalc(5);
        Assert.AreEqual(35, Result, 'Both subscribers should have modified the value');
    end;

    [Test]
    procedure MultipleCallsFireBothSubscribersEachTime()
    var
        Publisher: Codeunit "MS Publisher";
        Result: Integer;
    begin
        // Positive: calling twice fires both subscribers each time
        Result := Publisher.DoCalc(0);
        Assert.AreEqual(30, Result, 'First call: 0+10+20=30');

        Result := Publisher.DoCalc(100);
        Assert.AreEqual(130, Result, 'Second call: 100+10+20=130');
    end;
}
