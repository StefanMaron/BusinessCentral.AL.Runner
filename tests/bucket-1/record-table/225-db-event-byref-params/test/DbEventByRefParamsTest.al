codeunit 59853 "DEBP Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Positive tests: subscriber with "var RunTrigger: Boolean" fires and
    // receives the correct values. This was crashing before the fix:
    //   ArgumentException: Object of type 'System.Boolean' cannot be converted
    //   to type 'Microsoft.Dynamics.Nav.Runtime.ByRef`1[System.Boolean]'
    // -----------------------------------------------------------------------

    [Test]
    procedure BeforeInsertEvent_VarRunTriggerSubscriberFires()
    var
        Src: Record "DEBP Source";
        Log: Record "DEBP Log";
    begin
        // Positive: subscriber with var RunTrigger fires on Insert
        Src.PK := 1;
        Src.Name := 'Alpha';
        Src.Insert();

        Assert.IsTrue(Log.Get(1), 'BeforeInsert log should exist');
        Assert.AreEqual('BeforeInsert', Log.EventType, 'EventType should be BeforeInsert');
        Assert.AreEqual('Alpha', Log.RecName, 'RecName should match Rec.Name');
    end;

    [Test]
    procedure AfterInsertEvent_VarRunTriggerSubscriberFires()
    var
        Src: Record "DEBP Source";
        Log: Record "DEBP Log";
    begin
        // Positive: subscriber with var RunTrigger fires on Insert (after)
        Src.PK := 1;
        Src.Name := 'Beta';
        Src.Insert();

        Assert.IsTrue(Log.Get(2), 'AfterInsert log should exist');
        Assert.AreEqual('AfterInsert', Log.EventType, 'EventType should be AfterInsert');
        Assert.AreEqual('Beta', Log.RecName, 'RecName should match');
    end;

    [Test]
    procedure BeforeModifyEvent_VarRunTriggerSubscriberFires()
    var
        Src: Record "DEBP Source";
        Log: Record "DEBP Log";
    begin
        // Positive: subscriber with var RunTrigger fires on Modify (before)
        Src.PK := 1;
        Src.Name := 'Gamma';
        Src.Insert();
        Src.Name := 'Delta';
        Src.Modify();

        Assert.IsTrue(Log.Get(3), 'BeforeModify log should exist');
        Assert.AreEqual('BeforeModify', Log.EventType, 'EventType should be BeforeModify');
    end;

    [Test]
    procedure AfterModifyEvent_VarRunTriggerSubscriberFires()
    var
        Src: Record "DEBP Source";
        Log: Record "DEBP Log";
    begin
        // Positive: subscriber with var RunTrigger fires on Modify (after)
        Src.PK := 1;
        Src.Name := 'Epsilon';
        Src.Insert();
        Src.Name := 'Zeta';
        Src.Modify();

        Assert.IsTrue(Log.Get(4), 'AfterModify log should exist');
        Assert.AreEqual('AfterModify', Log.EventType, 'EventType should be AfterModify');
        Assert.AreEqual('Zeta', Log.RecName, 'RecName should be modified value');
    end;

    [Test]
    procedure BeforeDeleteEvent_VarRunTriggerSubscriberFires()
    var
        Src: Record "DEBP Source";
        Log: Record "DEBP Log";
    begin
        // Positive: subscriber with var RunTrigger fires on Delete (before)
        Src.PK := 1;
        Src.Name := 'Eta';
        Src.Insert();
        Src.Delete();

        Assert.IsTrue(Log.Get(5), 'BeforeDelete log should exist');
        Assert.AreEqual('BeforeDelete', Log.EventType, 'EventType should be BeforeDelete');
        Assert.AreEqual('Eta', Log.RecName, 'RecName should match');
    end;

    [Test]
    procedure AfterDeleteEvent_VarRunTriggerSubscriberFires()
    var
        Src: Record "DEBP Source";
        Log: Record "DEBP Log";
    begin
        // Positive: subscriber with var RunTrigger fires on Delete (after)
        Src.PK := 1;
        Src.Name := 'Theta';
        Src.Insert();
        Src.Delete();

        Assert.IsTrue(Log.Get(6), 'AfterDelete log should exist');
        Assert.AreEqual('AfterDelete', Log.EventType, 'EventType should be AfterDelete');
        Assert.AreEqual('Theta', Log.RecName, 'RecName should match');
    end;

    // -----------------------------------------------------------------------
    // Negative: confirm the subscriber does NOT fire when the source table
    // is never touched (proves each test is isolated).
    // -----------------------------------------------------------------------

    [Test]
    procedure NoEventWithoutDatabaseOperation()
    var
        Log: Record "DEBP Log";
    begin
        // Negative: no DB operations on DEBP Source → no log entries
        Assert.IsFalse(Log.Get(1), 'Log PK=1 should not exist without insert');
        Assert.IsFalse(Log.Get(2), 'Log PK=2 should not exist without insert');
    end;
}
