codeunit 59966 "XR Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure XRecFromCodeHasNewValues()
    var
        Src: Record "XR Source";
        Log: Record "XR EventLog";
    begin
        // In BC, when modifying from code (Rec.Modify()), xRec has the SAME
        // values as Rec (new values). This is a known BC behavior — xRec is
        // only correctly populated with old values from page triggers.
        Src.PK := 1;
        Src.Name := 'Original';
        Src.Amount := 100;
        Src.Insert();

        Src.Name := 'Changed';
        Src.Amount := 200;
        Src.Modify();

        Assert.IsTrue(Log.Get(1), 'BeforeModify log should exist');
        Assert.AreEqual('Changed', Log.RecName, 'Rec.Name should be new value');
        // BC quirk: xRec from code has new values, not old
        Assert.AreEqual('Changed', Log.XRecName, 'xRec.Name from code should equal Rec.Name');
        Assert.AreEqual(200, Log.RecAmount, 'Rec.Amount should be new value');
        Assert.AreEqual(200, Log.XRecAmount, 'xRec.Amount from code should equal Rec.Amount');
    end;

    [Test]
    procedure XRecConsistentBetweenBeforeAndAfter()
    var
        Src: Record "XR Source";
        BeforeLog: Record "XR EventLog";
        AfterLog: Record "XR EventLog";
    begin
        // Both OnBefore and OnAfter should see the same xRec behavior.
        Src.PK := 1;
        Src.Name := 'Start';
        Src.Amount := 50;
        Src.Insert();

        Src.Name := 'End';
        Src.Amount := 150;
        Src.Modify();

        Assert.IsTrue(BeforeLog.Get(1), 'BeforeModify log should exist');
        Assert.IsTrue(AfterLog.Get(2), 'AfterModify log should exist');

        // Both events should see same xRec values (both = new values from code)
        Assert.AreEqual(BeforeLog.XRecName, AfterLog.XRecName, 'xRec should be consistent');
        Assert.AreEqual(BeforeLog.XRecAmount, AfterLog.XRecAmount, 'xRec.Amount should be consistent');
    end;

    [Test]
    procedure XRecNameAndAmountBothMatchRec()
    var
        Src: Record "XR Source";
        Log: Record "XR EventLog";
    begin
        // Verify that ALL fields in xRec match Rec (not just Name).
        Src.PK := 1;
        Src.Name := 'Alice';
        Src.Amount := 999;
        Src.Insert();

        Src.Name := 'Bob';
        Src.Amount := 42;
        Src.Modify();

        // Check AfterModify log (PK=2)
        Assert.IsTrue(Log.Get(2), 'AfterModify log should exist');
        Assert.AreEqual('Bob', Log.RecName, 'Rec.Name should be Bob');
        Assert.AreEqual('Bob', Log.XRecName, 'xRec.Name should also be Bob (code path)');
        Assert.AreEqual(42, Log.RecAmount, 'Rec.Amount should be 42');
        Assert.AreEqual(42, Log.XRecAmount, 'xRec.Amount should also be 42 (code path)');
    end;
}
