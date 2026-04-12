codeunit 56810 "Isolation Probe Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Probe: Codeunit "Isolation Probe";

    [Test]
    procedure RecordReadIsolationDoesNotCrash()
    begin
        // Positive: setting ReadIsolation on a Record should be a no-op
        Probe.SetRecordReadIsolation();
        Assert.IsTrue(true, 'Record.ReadIsolation should not crash');
    end;

    [Test]
    procedure RecRefReadIsolationDoesNotCrash()
    begin
        // Positive: setting ReadIsolation on a RecordRef should be a no-op
        Probe.SetRecRefReadIsolation();
        Assert.IsTrue(true, 'RecordRef.ReadIsolation should not crash');
    end;

    [Test]
    procedure RecRefDuplicateSharesTable()
    begin
        // Positive: Duplicate returns a copy that sees the same table data
        Assert.AreEqual(1, Probe.DuplicateRecRef(), 'Duplicate RecRef should see 1 record');
    end;

    [Test]
    procedure InStreamAssignCopiesData()
    begin
        // Positive: InStr2 := InStr1 should copy the stream so ReadText works
        Probe.AssignInStream();
        Assert.IsTrue(true, 'InStream assign should not crash');
    end;

    [Test]
    procedure RecRefDuplicateOnClosedRefIsEmpty()
    var
        RecRef: RecordRef;
        RecRef2: RecordRef;
    begin
        // Negative: Duplicate on a RecRef that was never opened returns empty
        RecRef2 := RecRef.Duplicate();
        Assert.AreEqual(0, RecRef2.Count(), 'Duplicate of unopened RecRef should have count 0');
    end;
}
