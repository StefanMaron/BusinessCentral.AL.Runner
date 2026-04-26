codeunit 59962 "BDE Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure OnBeforeInsertEventFires()
    var
        Src: Record "BDE Source";
        Log: Record "BDE EventLog";
    begin
        // Positive: OnBeforeInsertEvent fires when Insert() is called.
        Src.PK := 1;
        Src.Name := 'Alice';
        Src.Insert();

        Assert.IsTrue(Log.Get(1), 'OnBeforeInsertEvent should have fired (Log PK=1)');
        Assert.AreEqual('BeforeInsert', Log.EventType, 'Event type should be BeforeInsert');
        Assert.AreEqual('Alice', Log.RecName, 'Rec.Name should be passed to subscriber');
    end;

    [Test]
    procedure OnBeforeModifyEventFires()
    var
        Src: Record "BDE Source";
        Log: Record "BDE EventLog";
    begin
        // Positive: OnBeforeModifyEvent fires when Modify() is called.
        Src.PK := 1;
        Src.Name := 'Original';
        Src.Insert();

        Src.Name := 'Changed';
        Src.Modify();

        Assert.IsTrue(Log.Get(3), 'OnBeforeModifyEvent should have fired (Log PK=3)');
        Assert.AreEqual('BeforeModify', Log.EventType, 'Event type should be BeforeModify');
        Assert.AreEqual('Changed', Log.RecName, 'Rec.Name should be the new value');
    end;

    [Test]
    procedure OnBeforeDeleteEventFires()
    var
        Src: Record "BDE Source";
        Log: Record "BDE EventLog";
    begin
        // Positive: OnBeforeDeleteEvent fires when Delete() is called.
        Src.PK := 1;
        Src.Name := 'ToDelete';
        Src.Insert();
        Src.Delete();

        Assert.IsTrue(Log.Get(5), 'OnBeforeDeleteEvent should have fired (Log PK=5)');
        Assert.AreEqual('BeforeDelete', Log.EventType, 'Event type should be BeforeDelete');
        Assert.AreEqual('ToDelete', Log.RecName, 'Rec.Name should be passed');
    end;

    [Test]
    procedure InsertFiresBothBeforeAndAfter()
    var
        Src: Record "BDE Source";
        BeforeLog: Record "BDE EventLog";
        AfterLog: Record "BDE EventLog";
    begin
        // Positive: both OnBefore and OnAfter fire for Insert.
        Src.PK := 1;
        Src.Name := 'Both';
        Src.Insert();

        Assert.IsTrue(BeforeLog.Get(1), 'OnBeforeInsertEvent should have fired');
        Assert.AreEqual('BeforeInsert', BeforeLog.EventType, 'Should be BeforeInsert');

        Assert.IsTrue(AfterLog.Get(2), 'OnAfterInsertEvent should have fired');
        Assert.AreEqual('AfterInsert', AfterLog.EventType, 'Should be AfterInsert');
    end;

    [Test]
    procedure ModifyFiresBothBeforeAndAfter()
    var
        Src: Record "BDE Source";
        BeforeLog: Record "BDE EventLog";
        AfterLog: Record "BDE EventLog";
    begin
        Src.PK := 1;
        Src.Name := 'Orig';
        Src.Insert();

        Src.Name := 'New';
        Src.Modify();

        Assert.IsTrue(BeforeLog.Get(3), 'OnBeforeModifyEvent should have fired');
        Assert.AreEqual('BeforeModify', BeforeLog.EventType, 'Should be BeforeModify');

        Assert.IsTrue(AfterLog.Get(4), 'OnAfterModifyEvent should have fired');
        Assert.AreEqual('AfterModify', AfterLog.EventType, 'Should be AfterModify');
    end;

    [Test]
    procedure DeleteFiresBothBeforeAndAfter()
    var
        Src: Record "BDE Source";
        BeforeLog: Record "BDE EventLog";
        AfterLog: Record "BDE EventLog";
    begin
        Src.PK := 1;
        Src.Name := 'Del';
        Src.Insert();
        Src.Delete();

        Assert.IsTrue(BeforeLog.Get(5), 'OnBeforeDeleteEvent should have fired');
        Assert.AreEqual('BeforeDelete', BeforeLog.EventType, 'Should be BeforeDelete');

        Assert.IsTrue(AfterLog.Get(6), 'OnAfterDeleteEvent should have fired');
        Assert.AreEqual('AfterDelete', AfterLog.EventType, 'Should be AfterDelete');
    end;

    [Test]
    procedure NoEventLogForUnsubscribedEvent()
    var
        Src: Record "BDE Source";
        Log: Record "BDE EventLog";
    begin
        // Negative: only events with subscribers produce log entries.
        Src.PK := 1;
        Src.Name := 'Test';
        Src.Insert();

        // Log should only have BeforeInsert (PK=1) and AfterInsert (PK=2)
        Log.SetRange(EventType, 'BeforeModify');
        Assert.IsFalse(Log.FindFirst(), 'No Modify happened — no BeforeModify log');
    end;
}
