codeunit 56633 "OI Probe Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure OnInsertSetsTraceField()
    var
        R: Record "OI Probe Row";
    begin
        R.Id := 1;
        R.Insert(true);
        Assert.AreEqual('touched', R.Trace, 'OnInsert must populate Trace');
    end;

    [Test]
    procedure OnInsertWithoutRunTriggerFlagSkipsTrigger()
    var
        R: Record "OI Probe Row";
    begin
        // Insert(false) — explicitly skip the trigger
        R.Id := 2;
        R.Insert(false);
        Assert.AreEqual('', R.Trace, 'Insert(false) must not run OnInsert');
    end;

    [Test]
    procedure OnInsertCounterIncrementsAcrossInserts()
    var
        A: Record "OI With Side Effect";
        B: Record "OI With Side Effect";
        Counter: Record "OI Counter";
    begin
        A.Id := 1; A.Insert(true);
        B.Id := 2; B.Insert(true);
        Counter.Get(1);
        Assert.AreEqual(2, Counter.Count, 'OnInsert should have incremented the counter twice');
    end;
}
