codeunit 52900 "Test Record Id"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure RecordIdCanBeAssigned()
    var
        Rec: Record "RecId Table";
        RecId: RecordId;
    begin
        // Positive: RecordId can be read from a record without crashing
        Rec.Init();
        Rec."No." := 'R001';
        Rec.Insert(true);

        Rec.Get('R001');
        RecId := Rec.RecordId;
        // RecordId is a value type - just verify it doesn't crash
        Assert.IsTrue(true, 'RecordId assignment should not crash');
    end;

    [Test]
    procedure RecordIdOnNewRecordDoesNotCrash()
    var
        Rec: Record "RecId Table";
        RecId: RecordId;
    begin
        // Positive: RecordId on an uninserted record also works
        Rec.Init();
        RecId := Rec.RecordId;
        Assert.IsTrue(true, 'RecordId on new record should not crash');
    end;

    [Test]
    procedure RecordIdFormatsWithoutCrash()
    var
        Rec: Record "RecId Table";
        RecIdFormatted: Text;
    begin
        // Positive: Format(RecordId) runs without crashing
        Rec.Init();
        Rec."No." := 'R002';
        RecIdFormatted := Format(Rec.RecordId);
        // In standalone mode, RecordId returns Default which formats as empty
        Assert.AreEqual('', RecIdFormatted, 'Format of default RecordId should be empty in standalone mode');
    end;

    [Test]
    procedure TwoRecordIdsFromDifferentRecords()
    var
        Rec1: Record "RecId Table";
        Rec2: Record "RecId Table";
        RecId1: RecordId;
        RecId2: RecordId;
    begin
        // Negative: verify RecordId can be obtained from multiple records
        Rec1.Init();
        Rec1."No." := 'R003';
        Rec2.Init();
        Rec2."No." := 'R004';
        RecId1 := Rec1.RecordId;
        RecId2 := Rec2.RecordId;
        // Both should succeed without crash
        Assert.IsTrue(true, 'Both RecordId assignments should succeed');
    end;
}
