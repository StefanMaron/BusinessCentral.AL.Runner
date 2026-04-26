codeunit 56271 "API Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Probe: Codeunit "API Probe";

    // === 1. Ascending ===

    [Test]
    procedure AscendingDefaultIsTrue()
    begin
        // [GIVEN] A freshly opened RecRef
        // [THEN] Ascending defaults to true
        Assert.IsTrue(Probe.GetAscendingDefault(), 'Default Ascending must be true');
    end;

    [Test]
    procedure SetAscendingFalseReversesSortOrder()
    begin
        // [GIVEN] 3 entries with Entry No. 1, 2, 3
        Probe.InsertEntry(1, 'A', 'First', 10.0, true);
        Probe.InsertEntry(2, 'B', 'Second', 20.0, false);
        Probe.InsertEntry(3, 'C', 'Third', 30.0, true);

        // [WHEN] Iterating with Ascending=false
        // [THEN] Entry numbers come in descending order
        Assert.AreEqual('3,2,1', Probe.IterateFieldValuesDescending(1),
            'Descending iteration must reverse the order');
    end;

    // === 2. Mark / MarkedOnly ===

    [Test]
    procedure MarkRecordAndRetrieve()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        // [GIVEN] 2 entries
        Probe.InsertEntry(1, 'A', 'Alpha', 10.0, true);
        Probe.InsertEntry(2, 'B', 'Bravo', 20.0, false);

        // [WHEN] Mark entry 1
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(1);
        RecRef.FindFirst();
        RecRef.Mark(true);

        // [THEN] Mark() returns true for entry 1
        Assert.IsTrue(RecRef.Mark(), 'Marked record must return true');

        // [THEN] Mark() returns false for entry 2
        FldRef.SetRange(2);
        RecRef.FindFirst();
        Assert.IsFalse(RecRef.Mark(), 'Unmarked record must return false');
        RecRef.Close();
    end;

    [Test]
    procedure MarkedOnlyFiltersIteration()
    begin
        // [GIVEN] 3 entries
        Probe.InsertEntry(1, 'A', 'Alpha', 10.0, true);
        Probe.InsertEntry(2, 'B', 'Bravo', 20.0, false);
        Probe.InsertEntry(3, 'C', 'Charlie', 30.0, true);

        // [WHEN] Mark entries 1 and 3, then iterate with MarkedOnly
        // [THEN] Only entries 1 and 3 are returned
        Assert.AreEqual('1,3', Probe.IterateMarkedOnly(),
            'MarkedOnly must filter to only marked records');
    end;

    [Test]
    procedure ClearMarksResetsAll()
    begin
        // [GIVEN] An entry exists and is marked
        Probe.InsertEntry(1, 'A', 'Alpha', 10.0, true);

        // [WHEN] Mark then ClearMarks
        // [THEN] Mark() returns false
        Assert.IsFalse(Probe.MarkAndClearMarks(),
            'After ClearMarks, no records should be marked');
    end;

    // === 3. FieldRef.GetFilter ===

    [Test]
    procedure GetFilterReturnsRangeExpression()
    begin
        // [WHEN] SetRange(10, 50) on Entry No.
        // [THEN] GetFilter returns "10..50"
        Assert.AreEqual('10..50', Probe.GetFilterAfterSetRange(),
            'GetFilter must return range expression');
    end;

    [Test]
    procedure GetFilterReturnsSingleValue()
    begin
        // [WHEN] SetRange(42) on Entry No.
        // [THEN] GetFilter returns "42"
        Assert.AreEqual('42', Probe.GetFilterAfterSetRangeSingle(),
            'GetFilter for single value must return just the value');
    end;

    [Test]
    procedure GetFilterReturnsFilterExpression()
    begin
        // [WHEN] SetFilter('*test*') on Description
        // [THEN] GetFilter returns "*test*"
        Assert.AreEqual('*test*', Probe.GetFilterAfterSetFilter(),
            'GetFilter must return filter expression');
    end;

    // === 4. FieldRef.GetRangeMin / GetRangeMax ===

    [Test]
    procedure GetRangeMinMax()
    begin
        // [WHEN] SetRange(10, 50) on Entry No.
        // [THEN] GetRangeMin=10, GetRangeMax=50
        Assert.AreEqual(10, Probe.GetRangeMinAfterSetRange(),
            'GetRangeMin must return the from value');
        Assert.AreEqual(50, Probe.GetRangeMaxAfterSetRange(),
            'GetRangeMax must return the to value');
    end;

    [Test]
    procedure GetRangeMinMaxSingleValue()
    begin
        // [WHEN] SetRange(10) on Entry No. (single value => from=to=10)
        // [THEN] Both min and max return 10
        Assert.AreEqual('10,10', Probe.GetRangeMinMaxSingle(),
            'Both GetRangeMin and GetRangeMax must return the same value for single-value range');
    end;

    // === 5. FieldRef.Record ===

    [Test]
    procedure FieldRefRecordReturnsOwner()
    begin
        // [WHEN] Get FieldRef from RecRef on API Test Entry
        // [THEN] FieldRef.Record().Number matches the table ID
        Assert.AreEqual(56270, Probe.FieldRefRecordOwner(),
            'FieldRef.Record must return the owning RecordRef with correct table number');
    end;

    // === 6. KeyRef ===

    [Test]
    procedure KeyCountReturnsOne()
    begin
        // [THEN] KeyCount >= 1 (we expose at least the PK)
        Assert.IsTrue(Probe.GetKeyCount() >= 1,
            'KeyCount must be at least 1');
    end;

    [Test]
    procedure KeyRefFieldCountMatchesPK()
    begin
        // [GIVEN] Table has composite PK (Entry No., Category) = 2 fields
        // [THEN] KeyIndex(1).FieldCount = 2
        Assert.AreEqual(2, Probe.GetKeyFieldCount(),
            'KeyRef.FieldCount must match PK field count');
    end;

    [Test]
    procedure KeyRefFieldIndexReturnsCorrectField()
    begin
        // [GIVEN] PK fields are Entry No. (1) and Category (2)
        // [THEN] FieldIndex(1).Number = 1, FieldIndex(2).Number = 2
        Assert.AreEqual(1, Probe.GetKeyFieldNo(1),
            'First PK field must be field 1');
        Assert.AreEqual(2, Probe.GetKeyFieldNo(2),
            'Second PK field must be field 2');
    end;

    // === 7. CurrentKeyIndex ===

    [Test]
    procedure CurrentKeyIndexReturnsOne()
    begin
        // [THEN] CurrentKeyIndex defaults to 1
        Assert.AreEqual(1, Probe.GetCurrentKeyIndex(),
            'CurrentKeyIndex must return 1');
    end;

    // === Negative Tests ===

    [Test]
    procedure GetFilterReturnsEmptyWhenNoFilter()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        // [GIVEN] No filter set
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        // [THEN] GetFilter returns empty string
        Assert.AreEqual('', FldRef.GetFilter(),
            'GetFilter with no active filter must return empty string');
        RecRef.Close();
    end;

    [Test]
    procedure GetRangeMinReturnsDefaultWhenNoRange()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Val: Integer;
    begin
        // [GIVEN] No range set
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        // [THEN] GetRangeMin returns 0 (default for integer)
        Val := FldRef.GetRangeMin();
        Assert.AreEqual(0, Val, 'GetRangeMin with no range must return default');
        RecRef.Close();
    end;

    [Test]
    procedure RecRefGetFilterDelegatesToHandle()
    begin
        // [WHEN] SetRange on a field via FieldRef, then read via FieldRef.GetFilter
        // [THEN] Returns the same range expression — exercises MockRecordRef's delegation to handle
        Assert.AreEqual('5..15', Probe.GetRecRefGetFilter(1),
            'FieldRef.GetFilter must return the range set on the field');
    end;

    [Test]
    procedure RecRefGetFilterReturnsEmptyWhenNoFilter()
    begin
        // [GIVEN] No filter set on the field
        // [THEN] FieldRef.GetFilter returns empty string (via MockRecordRef delegation)
        Assert.AreEqual('', Probe.GetRecRefGetFilterNoFilter(1),
            'FieldRef.GetFilter must return empty string when no filter is set');
    end;

    [Test]
    procedure FindLastWithMarkedOnlyReturnsLastMarkedRecord()
    begin
        // [GIVEN] 3 entries (Entry No. 1, 2, 3); the probe marks only entry 2 via RecordRef.Mark
        // Note: the last parameter to InsertEntry is the 'Active' field, not the mark flag
        Probe.InsertEntry(1, 'A', 'Alpha', 10.0, true);
        Probe.InsertEntry(2, 'B', 'Bravo', 20.0, false);
        Probe.InsertEntry(3, 'C', 'Charlie', 30.0, true);
        // [WHEN] FindLast with MarkedOnly=true (probe internally marks entry 2)
        // [THEN] FindLast returns entry 2 — the only (and thus last) marked record
        Assert.AreEqual('2', Probe.FindLastEntryNoWithMarkedOnly(),
            'FindLast with MarkedOnly must return the last marked record');
    end;

    [Test]
    procedure MarkUnmarkedRecordReturnsFalse()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        // [GIVEN] An entry exists but is not marked
        Probe.InsertEntry(1, 'A', 'Alpha', 10.0, true);
        RecRef.Open(Database::"API Test Entry");
        RecRef.FindFirst();
        // [THEN] Mark() returns false
        Assert.IsFalse(RecRef.Mark(), 'Unmarked record must return false');
        RecRef.Close();
    end;
}
