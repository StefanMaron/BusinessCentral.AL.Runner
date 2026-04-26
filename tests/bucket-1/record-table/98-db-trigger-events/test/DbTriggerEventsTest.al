codeunit 59802 "DTE Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure OnAfterInsertEventFires()
    var
        Src: Record "DTE Source";
        C: Record "DTE Counter";
    begin
        // Positive: inserting a record fires OnAfterInsertEvent subscriber
        Src.PK := 1;
        Src.Name := 'Test';
        Src.Insert();

        Assert.IsTrue(C.Get(1), 'Counter should exist after insert event');
        Assert.AreEqual(1, C.InsertCount, 'Insert subscriber should have fired once');
    end;

    [Test]
    procedure OnAfterModifyEventFires()
    var
        Src: Record "DTE Source";
        C: Record "DTE Counter";
    begin
        // Positive: modifying a record fires OnAfterModifyEvent subscriber
        Src.PK := 1;
        Src.Name := 'Original';
        Src.Insert();

        Src.Name := 'Modified';
        Src.Modify();

        Assert.IsTrue(C.Get(1), 'Counter should exist after events');
        Assert.AreEqual(1, C.ModifyCount, 'Modify subscriber should have fired once');
    end;

    [Test]
    procedure OnAfterDeleteEventFires()
    var
        Src: Record "DTE Source";
        C: Record "DTE Counter";
    begin
        // Positive: deleting a record fires OnAfterDeleteEvent subscriber
        Src.PK := 1;
        Src.Name := 'ToDelete';
        Src.Insert();

        Src.Delete();

        Assert.IsTrue(C.Get(1), 'Counter should exist after events');
        Assert.AreEqual(1, C.DeleteCount, 'Delete subscriber should have fired once');
    end;

    [Test]
    procedure MultipleInsertsFireMultipleEvents()
    var
        Src: Record "DTE Source";
        C: Record "DTE Counter";
    begin
        // Positive: each insert fires its own event
        Src.PK := 1;
        Src.Insert();
        Src.PK := 2;
        Src.Insert();
        Src.PK := 3;
        Src.Insert();

        Assert.IsTrue(C.Get(1), 'Counter should exist');
        Assert.AreEqual(3, C.InsertCount, 'Insert subscriber should have fired 3 times');
    end;

    [Test]
    procedure NoEventWithoutSubscriber()
    var
        Src: Record "DTE Source";
        C: Record "DTE Counter";
    begin
        // Negative: counter table should have no records when no operations
        // were done on the source table. This proves the counter is reset
        // between tests and only incremented by event subscribers.
        Assert.IsFalse(C.Get(1), 'Counter should not exist without source operations');
    end;
}
