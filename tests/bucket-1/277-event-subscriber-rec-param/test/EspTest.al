/// Event Subscriber Record Parameter tests (issue #818).
codeunit 118003 "ESP Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;

    local procedure InsertItem(Id: Integer; Price: Decimal)
    var
        Item: Record "ESP Item";
    begin
        Item.Id := Id;
        Item.Name := 'Original';
        Item.Price := Price;
        Item.Insert();
    end;

    // ── Subscriber receives and modifies var Rec ──────────────────────────────

    [Test]
    procedure Subscriber_CanReadRecordField()
    var
        Pub: Codeunit "ESP Publisher";
        Item: Record "ESP Item";
    begin
        // [GIVEN] An item with price 50 (below the cancellation threshold)
        InsertItem(1, 50);

        // [WHEN] ProcessItem fires, subscriber reads Price and sets Name
        Pub.ProcessItem(1);

        // [THEN] The record's Name was modified by the subscriber
        Item.Get(1);
        Assert.AreEqual('AfterProcessTouched', Item.Name,
            'AfterProcess subscriber must have modified Name on the var Rec parameter');
    end;

    [Test]
    procedure Subscriber_CanCancelViaVarBoolean()
    var
        Pub: Codeunit "ESP Publisher";
        Cancelled: Boolean;
    begin
        // [GIVEN] An item with price 200 (above the threshold)
        InsertItem(2, 200);

        // [WHEN] ProcessItem fires, subscriber sets Cancel := true
        Cancelled := not Pub.ProcessItem(2);

        // [THEN] ProcessItem returned false (subscriber cancelled it)
        Assert.IsTrue(Cancelled, 'Subscriber must have cancelled via var Cancel parameter');
    end;

    [Test]
    procedure Subscriber_DoesNotCancelBelowThreshold()
    var
        Pub: Codeunit "ESP Publisher";
        Processed: Boolean;
    begin
        // [GIVEN] An item with price 50 (below the threshold)
        InsertItem(3, 50);

        // [WHEN] ProcessItem is called
        Processed := Pub.ProcessItem(3);

        // [THEN] Not cancelled — processed successfully
        Assert.IsTrue(Processed, 'ProcessItem must succeed when price <= 100');
    end;

    [Test]
    procedure Subscriber_RecModificationSeenByPublisher()
    var
        Pub: Codeunit "ESP Publisher";
        Item: Record "ESP Item";
    begin
        // [GIVEN] An item with price 10
        InsertItem(4, 10);

        // [WHEN] ProcessItem fires (subscriber touches the record, doesn't cancel)
        Pub.ProcessItem(4);

        // [THEN] OnAfterProcess subscriber modified Name
        Item.Get(4);
        Assert.AreEqual('AfterProcessTouched', Item.Name,
            'AfterProcess subscriber must have set Name');
    end;

    [Test]
    procedure Subscriber_SenderReceived_CanCallPublisherMethod()
    var
        Pub: Codeunit "ESP Publisher";
    begin
        // [GIVEN] An item with price 10
        InsertItem(5, 10);

        // [WHEN] ProcessItem fires the OnAfterProcess event with IncludeSender=true
        Pub.ProcessItem(5);

        // [THEN] The subscriber called Sender.SetTag() on the publisher instance
        Assert.AreEqual('AfterProcessFired', Pub.GetTag(),
            'Subscriber must have called SetTag on the Sender (publisher instance)');
    end;
}
