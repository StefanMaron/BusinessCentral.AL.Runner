codeunit 56871 "SR Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // --- Integer field SetRange ---

    [Test]
    procedure SetRangeIntegerMatchesRow()
    var
        Probe: Codeunit "SR Probe";
    begin
        // [GIVEN] Two rows with different Ids
        Probe.InsertRow(1, 'Alpha', 'A001', 0, 10.0);
        Probe.InsertRow(2, 'Bravo', 'B002', 1, 20.0);

        // [WHEN] SetRange on Integer field with value 1
        // [THEN] Exactly 1 row matches
        Assert.AreEqual(1, Probe.FilteredCountByInt(1), 'SetRange(Integer) must filter to 1 row');
    end;

    [Test]
    procedure SetRangeIntegerNoMatch()
    var
        Probe: Codeunit "SR Probe";
    begin
        // [GIVEN] Two rows with Ids 1 and 2
        Probe.InsertRow(1, 'Alpha', 'A001', 0, 10.0);
        Probe.InsertRow(2, 'Bravo', 'B002', 1, 20.0);

        // [WHEN] SetRange on Integer field with value 99 (no match)
        // [THEN] Count is 0
        Assert.AreEqual(0, Probe.FilteredCountByInt(99), 'SetRange(Integer) with no match must return 0');
    end;

    // --- Code field SetRange ---

    [Test]
    procedure SetRangeCodeMatchesRow()
    var
        Probe: Codeunit "SR Probe";
    begin
        // [GIVEN] Two rows with different Codes
        Probe.InsertRow(10, 'Alpha', 'A001', 0, 10.0);
        Probe.InsertRow(20, 'Bravo', 'B002', 1, 20.0);

        // [WHEN] SetRange on Code field with value 'A001'
        // [THEN] Exactly 1 row matches
        Assert.AreEqual(1, Probe.FilteredCountByCode('A001'), 'SetRange(Code) must filter to 1 row');
    end;

    [Test]
    procedure SetRangeCodeNoMatch()
    var
        Probe: Codeunit "SR Probe";
    begin
        // [GIVEN] Two rows
        Probe.InsertRow(10, 'Alpha', 'A001', 0, 10.0);
        Probe.InsertRow(20, 'Bravo', 'B002', 1, 20.0);

        // [WHEN] SetRange on Code field with value 'ZZZZZ' (no match)
        // [THEN] Count is 0
        Assert.AreEqual(0, Probe.FilteredCountByCode('ZZZZZ'), 'SetRange(Code) with no match must return 0');
    end;

    // --- Option field SetRange ---

    [Test]
    procedure SetRangeOptionMatchesRow()
    var
        Probe: Codeunit "SR Probe";
        SRTestItem: Record "SR Test Item";
    begin
        // [GIVEN] Two rows: one Open (0), one Closed (1)
        Probe.InsertRow(100, 'Alpha', 'A001', SRTestItem.Status::Open, 10.0);
        Probe.InsertRow(200, 'Bravo', 'B002', SRTestItem.Status::Closed, 20.0);

        // [WHEN] SetRange on Option field with Closed (1)
        // [THEN] Exactly 1 row matches
        Assert.AreEqual(1, Probe.FilteredCountByOption(SRTestItem.Status::Closed), 'SetRange(Option) must filter to 1 row');
    end;

    [Test]
    procedure SetRangeOptionNoMatch()
    var
        Probe: Codeunit "SR Probe";
        SRTestItem: Record "SR Test Item";
    begin
        // [GIVEN] Two rows: both Open (0)
        Probe.InsertRow(100, 'Alpha', 'A001', SRTestItem.Status::Open, 10.0);
        Probe.InsertRow(200, 'Bravo', 'B002', SRTestItem.Status::Open, 20.0);

        // [WHEN] SetRange on Option field with Pending (2) — no match
        // [THEN] Count is 0
        Assert.AreEqual(0, Probe.FilteredCountByOption(SRTestItem.Status::Pending), 'SetRange(Option) with no match must return 0');
    end;

    // --- Decimal field SetRange (range variant) ---

    [Test]
    procedure SetRangeDecimalRangeMatchesRows()
    var
        Probe: Codeunit "SR Probe";
    begin
        // [GIVEN] Three rows with Amounts 10, 20, 30
        Probe.InsertRow(1, 'Alpha', 'A001', 0, 10.0);
        Probe.InsertRow(2, 'Bravo', 'B002', 1, 20.0);
        Probe.InsertRow(3, 'Charlie', 'C003', 0, 30.0);

        // [WHEN] SetRange on Decimal field with range [10..20]
        // [THEN] 2 rows match
        Assert.AreEqual(2, Probe.FilteredCountByDecimalRange(10.0, 20.0), 'SetRange(Decimal, Decimal) must filter to 2 rows');
    end;

    [Test]
    procedure SetRangeDecimalRangeNoMatch()
    var
        Probe: Codeunit "SR Probe";
    begin
        // [GIVEN] Three rows with Amounts 10, 20, 30
        Probe.InsertRow(1, 'Alpha', 'A001', 0, 10.0);
        Probe.InsertRow(2, 'Bravo', 'B002', 1, 20.0);
        Probe.InsertRow(3, 'Charlie', 'C003', 0, 30.0);

        // [WHEN] SetRange on Decimal field with range [50..100] — no match
        // [THEN] Count is 0
        Assert.AreEqual(0, Probe.FilteredCountByDecimalRange(50.0, 100.0), 'SetRange(Decimal, Decimal) with no match must return 0');
    end;
}
