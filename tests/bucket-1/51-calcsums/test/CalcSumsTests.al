codeunit 55701 "CalcSums Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Record.CalcSums — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CalcSums_DecimalField_ReturnsCorrectSum()
    var
        Entry: Record "CalcSums Entry";
    begin
        // [GIVEN] Three entries with Amount 10.5, 20.0, 30.75
        InsertEntry(1, 1, 10.5, 0);
        InsertEntry(2, 1, 20.0, 0);
        InsertEntry(3, 1, 30.75, 0);

        // [WHEN] CalcSums on Amount (no filter — all records)
        Entry.CalcSums(Amount);

        // [THEN] Amount = 61.25
        Assert.AreEqual(61.25, Entry.Amount, 'CalcSums Decimal: sum should be 61.25');
    end;

    [Test]
    procedure CalcSums_IntegerField_ReturnsCorrectSum()
    var
        Entry: Record "CalcSums Entry";
    begin
        // [GIVEN] Three entries with Quantity 5, 10, 15
        InsertEntry(4, 1, 0, 5);
        InsertEntry(5, 1, 0, 10);
        InsertEntry(6, 1, 0, 15);

        // [WHEN] CalcSums on Quantity (no filter — all records)
        Entry.CalcSums(Quantity);

        // [THEN] Quantity = 30
        Assert.AreEqual(30, Entry.Quantity, 'CalcSums Integer: sum should be 30');
    end;

    [Test]
    procedure CalcSums_WithFilter_SumsOnlyMatchingRecords()
    var
        Entry: Record "CalcSums Entry";
    begin
        // [GIVEN] Two status=1 entries (Amount 100, 200) and one status=2 (Amount 999)
        InsertEntry(10, 1, 100, 0);
        InsertEntry(11, 1, 200, 0);
        InsertEntry(12, 2, 999, 0);

        // [WHEN] Filter to Status=1 then CalcSums
        Entry.SetRange(Status, 1);
        Entry.CalcSums(Amount);

        // [THEN] Amount = 300, not 1299
        Assert.AreEqual(300, Entry.Amount, 'CalcSums with filter: should sum only Status=1 records');
    end;

    [Test]
    procedure CalcSums_MultipleFields_BothUpdated()
    var
        Entry: Record "CalcSums Entry";
    begin
        // [GIVEN] Two entries
        InsertEntry(20, 1, 50.5, 3);
        InsertEntry(21, 1, 49.5, 7);

        // [WHEN] CalcSums on both Amount and Quantity
        Entry.CalcSums(Amount, Quantity);

        // [THEN] Amount = 100, Quantity = 10
        Assert.AreEqual(100, Entry.Amount, 'CalcSums multi: Amount should be 100');
        Assert.AreEqual(10, Entry.Quantity, 'CalcSums multi: Quantity should be 10');
    end;

    // -----------------------------------------------------------------------
    // Record.CalcSums — negative / edge-case tests
    // -----------------------------------------------------------------------

    [Test]
    procedure CalcSums_EmptyResult_ReturnsZero()
    var
        Entry: Record "CalcSums Entry";
    begin
        // [GIVEN] No records exist matching Status = 999
        // (table is empty for this value — no inserts)

        // [WHEN] SetRange to a non-existent status, then CalcSums
        Entry.SetRange(Status, 999);
        Entry.CalcSums(Amount);

        // [THEN] Amount is 0 (no records to sum)
        Assert.AreEqual(0, Entry.Amount, 'CalcSums empty: should return 0 when no records match');
    end;

    [Test]
    procedure CalcSums_FilterExcludesAll_QuantityZero()
    var
        Entry: Record "CalcSums Entry";
    begin
        // [GIVEN] One entry with Status=1
        InsertEntry(30, 1, 42, 7);

        // [WHEN] Filter to Status=2 (no match)
        Entry.SetRange(Status, 2);
        Entry.CalcSums(Quantity);

        // [THEN] Quantity = 0
        Assert.AreEqual(0, Entry.Quantity, 'CalcSums filter excludes all: Quantity should be 0');
    end;

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    local procedure InsertEntry(EntryNo: Integer; Status: Integer; Amount: Decimal; Quantity: Integer)
    var
        Entry: Record "CalcSums Entry";
    begin
        Entry.Init();
        Entry."Entry No." := EntryNo;
        Entry.Status := Status;
        Entry.Amount := Amount;
        Entry.Quantity := Quantity;
        Entry.Insert();
    end;
}
