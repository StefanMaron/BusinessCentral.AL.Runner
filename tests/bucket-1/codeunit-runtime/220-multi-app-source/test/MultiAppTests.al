codeunit 72002 "Multi App Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // Positive: helper codeunit from the same compilation returns expected value.
    [Test]
    procedure GetAnswerToEverything_Returns42()
    var
        Helper: Codeunit "Multi App Helper";
    begin
        Assert.AreEqual(42, Helper.GetAnswerToEverything(), 'Multi App Helper should return 42');
    end;

    // Positive: greeting function concatenates name correctly.
    [Test]
    procedure GetGreeting_ReturnsFormattedString()
    var
        Helper: Codeunit "Multi App Helper";
    begin
        Assert.AreEqual('Hello, World!', Helper.GetGreeting('World'), 'Greeting should include name');
    end;

    // Negative: greeting with empty name yields "Hello, !"
    [Test]
    procedure GetGreeting_EmptyName_YieldsEmptySlot()
    var
        Helper: Codeunit "Multi App Helper";
    begin
        Assert.AreEqual('Hello, !', Helper.GetGreeting(''), 'Empty name should produce "Hello, !"');
    end;

    // Positive: table from the same compilation can be instantiated and used.
    [Test]
    procedure MultiAppEntry_InsertAndFind()
    var
        Entry: Record "Multi App Entry";
    begin
        Entry.Id := 1;
        Entry.Value := 'test';
        Entry.Insert();

        Entry.Reset();
        Entry.SetRange(Id, 1);
        Assert.IsTrue(Entry.FindFirst(), 'Should find inserted entry');
        Assert.AreEqual('test', Entry.Value, 'Value should match what was inserted');
    end;
}
