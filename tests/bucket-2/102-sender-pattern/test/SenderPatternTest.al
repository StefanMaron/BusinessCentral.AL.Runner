codeunit 59958 "SP Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure IntegrationEventSenderReceived()
    var
        Publisher: Codeunit "SP Publisher";
        Result: Integer;
    begin
        // Positive: IntegrationEvent(true, false) passes the sender instance.
        // Subscriber reads sender.GetState() which publisher set to Value*2.
        Result := Publisher.Process(21);
        Assert.AreEqual(42, Result, 'Process should return Value*2');
        // If sender dispatch works, the subscriber was called without crashing.
        // The test proves the sender parameter is correctly forwarded.
    end;

    [Test]
    procedure IntegrationEventSenderNotPassedCrashes()
    var
        Publisher: Codeunit "SP Publisher";
    begin
        // Negative: if sender is NOT passed, the subscriber receives the first
        // event arg (Integer) where it expects MockCodeunitHandle, causing a
        // type conversion error. This test proves sender IS passed.
        // Without the fix, this entire test suite would crash.
        Publisher.Process(1);
        Assert.IsTrue(true, 'Should not crash — sender was forwarded correctly');
    end;

    [Test]
    procedure BusinessEventSenderReceived()
    var
        Publisher: Codeunit "SP BizPublisher";
    begin
        // Positive: BusinessEvent(true) also passes sender.
        Publisher.PostOrder(100);
        // If sender dispatch works, no crash occurs.
        Assert.AreEqual(100, Publisher.GetOrderNo(), 'Publisher should have set order no');
    end;

    [Test]
    procedure SenderWithVarParamsModifiesValue()
    var
        Publisher: Codeunit "SP MixedPublisher";
        Value: Integer;
        Ok: Boolean;
    begin
        // Positive: sender + var params work together.
        // Subscriber reads sender.GetTag() and when "override", sets Value=999.
        Publisher.SetTag('override');
        Value := 10;
        Ok := Publisher.Validate(Value);
        Assert.IsTrue(Ok, 'Validate should return true (handled by subscriber)');
        Assert.AreEqual(999, Value, 'Subscriber should have overridden value to 999');
    end;

    [Test]
    procedure SenderWithVarParamsNoOverride()
    var
        Publisher: Codeunit "SP MixedPublisher";
        Value: Integer;
        Ok: Boolean;
    begin
        // Negative: when sender.GetTag() is not "override", subscriber does nothing.
        // Normal validation runs (Value >= 0 → true).
        Publisher.SetTag('normal');
        Value := 10;
        Ok := Publisher.Validate(Value);
        Assert.IsTrue(Ok, 'Validate should return true (positive value)');
        Assert.AreEqual(10, Value, 'Value should be unchanged');
    end;

    [Test]
    procedure NoSenderStillWorks()
    var
        Publisher: Codeunit "SP NoSender Publisher";
    begin
        // Control: IntegrationEvent(false, false) without sender still works.
        // Ensures sender injection doesn't break non-sender events.
        Publisher.Fire(77);
        Assert.IsTrue(true, 'No-sender event should work unchanged');
    end;
}
