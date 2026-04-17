/// Tests for TestPage.GetRecord — proves the positioned record is returned.
/// GetRecord(var Rec) must populate Rec with the field values of the record
/// the TestPage is currently positioned on after GoToKey/GoToRecord.
codeunit 131002 "TGR Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: GetRecord returns the positioned record's field values
    // ------------------------------------------------------------------

    [Test]
    procedure GetRecord_AfterGoToKey_ReturnsPositionedRecord()
    var
        Inserted: Record "TGR Table";
        Fetched: Record "TGR Table";
        TP: TestPage "TGR Page";
    begin
        // [GIVEN] A record with Id=1 and Name='Alice' is inserted
        Inserted.Id := 1;
        Inserted.Name := 'Alice';
        Inserted.Insert();

        // [WHEN] TestPage is opened and positioned via GoToKey, then GetRecord is called
        TP.OpenEdit();
        TP.GoToKey(1);
        TP.GetRecord(Fetched);
        TP.Close();

        // [THEN] The fetched record has the same Name as the inserted record
        Assert.AreEqual('Alice', Fetched.Name, 'GetRecord must return Name from positioned record');
    end;

    [Test]
    procedure GetRecord_AfterGoToRecord_ReturnsPositionedRecord()
    var
        Inserted: Record "TGR Table";
        Fetched: Record "TGR Table";
        TP: TestPage "TGR Page";
    begin
        // [GIVEN] A record with Id=2 and Name='Bob' is inserted
        Inserted.Id := 2;
        Inserted.Name := 'Bob';
        Inserted.Insert();

        // [WHEN] TestPage is opened and positioned via GoToRecord, then GetRecord is called
        TP.OpenEdit();
        TP.GoToRecord(Inserted);
        TP.GetRecord(Fetched);
        TP.Close();

        // [THEN] The fetched record has the same Name as the inserted record
        Assert.AreEqual('Bob', Fetched.Name, 'GetRecord must return Name from GoToRecord-positioned record');
    end;

    // ------------------------------------------------------------------
    // Negative: two different records produce different GetRecord results
    // ------------------------------------------------------------------

    [Test]
    procedure GetRecord_TwoDifferentRecords_ProduceDifferentResults()
    var
        Rec1: Record "TGR Table";
        Rec2: Record "TGR Table";
        Fetched1: Record "TGR Table";
        Fetched2: Record "TGR Table";
        TP: TestPage "TGR Page";
    begin
        // [GIVEN] Two records with distinct Names
        Rec1.Id := 10;
        Rec1.Name := 'First';
        Rec1.Insert();
        Rec2.Id := 20;
        Rec2.Name := 'Second';
        Rec2.Insert();

        // [WHEN] GetRecord is called after positioning on each
        TP.OpenEdit();
        TP.GoToKey(10);
        TP.GetRecord(Fetched1);
        TP.GoToKey(20);
        TP.GetRecord(Fetched2);
        TP.Close();

        // [THEN] The two fetched records differ
        Assert.AreNotEqual(Fetched1.Name, Fetched2.Name,
            'GetRecord must reflect the currently positioned record, not always the same one');
    end;
}
