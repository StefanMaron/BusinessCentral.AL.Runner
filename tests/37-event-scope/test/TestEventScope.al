codeunit 53700 "Test Event Scope"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure EventPublisherCodeunitCompiles()
    var
        Pub: Codeunit "Event Publisher";
        Result: Decimal;
    begin
        // Positive: codeunit with IntegrationEvent compiles and runs
        // The event publisher is a no-op, so CalcWithEvent just doubles the amount
        Result := Pub.CalcWithEvent(10);
        Assert.AreEqual(20, Result, 'Should be 10 * 2 = 20');
    end;

    [Test]
    procedure EventPublisherDoesNotAlterValue()
    var
        Pub: Codeunit "Event Publisher";
        Result: Decimal;
    begin
        // Negative: without subscribers, the event doesn't change the amount
        Result := Pub.CalcWithEvent(0);
        Assert.AreEqual(0, Result, 'Should be 0 * 2 = 0');
    end;
}
