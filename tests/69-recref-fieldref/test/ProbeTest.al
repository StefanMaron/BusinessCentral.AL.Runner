codeunit 56681 "RF Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure TableNoReturnsOpenedTable()
    var
        Probe: Codeunit "RF Probe";
    begin
        // [GIVEN] RecRef opened on table 56680
        // [THEN] Number() returns 56680
        Assert.AreEqual(56680, Probe.TableNoAfterOpen(56680), 'TableNo must match opened table');
    end;

    [Test]
    procedure InsertViaRecRefAndReadBack()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        // [GIVEN] A row inserted via RecordRef with Field().Value
        Probe.SetFieldAndInsert(56680, 1, 42, 2, 'Widget');

        // [THEN] Typed Record can read it back
        R.Get(42);
        Assert.AreEqual('Widget', R.Name, 'Name must match value set via FieldRef');
    end;

    [Test]
    procedure ReadFieldValueViaRecRef()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        // [GIVEN] A row inserted via typed Record
        R.Id := 10;
        R.Name := 'Gadget';
        R.Insert();

        // [WHEN] Reading via RecordRef.Field(2).Value
        // [THEN] The value matches
        Assert.AreEqual('Gadget', Probe.GetFieldValue(56680, 2), 'FieldRef.Value must return field content');
    end;

    [Test]
    procedure FieldNumberReturnsCorrectNo()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        // [GIVEN] A row exists
        R.Id := 1;
        R.Insert();

        // [THEN] FieldRef.Number() returns the field number
        Assert.AreEqual(2, Probe.FieldNumber(56680, 2), 'FieldRef.Number must return field number');
    end;

    [Test]
    procedure CountViaRecRef()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        // [GIVEN] 3 rows inserted via typed Record
        R.Id := 1; R.Insert();
        R.Id := 2; R.Insert();
        R.Id := 3; R.Insert();

        // [THEN] RecRef.Count() returns 3
        Assert.AreEqual(3, Probe.CountRecords(56680), 'Count must be 3');
    end;

    [Test]
    procedure EmptyTableCountIsZero()
    var
        Probe: Codeunit "RF Probe";
    begin
        // [GIVEN] No rows
        // [THEN] Count is 0
        Assert.AreEqual(0, Probe.CountRecords(56680), 'Count of empty table must be 0');
    end;

    // --- FindSet + Next iteration ---

    [Test]
    procedure FindSetNextIteratesAllRows()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        // [GIVEN] 3 rows with known names
        R.Id := 1; R.Name := 'Alpha'; R.Insert();
        R.Id := 2; R.Name := 'Bravo'; R.Insert();
        R.Id := 3; R.Name := 'Charlie'; R.Insert();

        // [WHEN] Iterating via RecRef FindSet + Next
        // [THEN] All names appear in order
        Assert.AreEqual('Alpha,Bravo,Charlie', Probe.IterateNames(56680), 'Must iterate all rows');
    end;

    [Test]
    procedure FindSetOnEmptyReturnsEmpty()
    var
        Probe: Codeunit "RF Probe";
    begin
        // [GIVEN] Empty table
        // [THEN] IterateNames returns empty string (FindSet returns false)
        Assert.AreEqual('', Probe.IterateNames(56680), 'Empty table must yield empty result');
    end;

    // --- IsEmpty ---

    [Test]
    procedure IsEmptyOnEmptyTable()
    var
        Probe: Codeunit "RF Probe";
    begin
        Assert.IsTrue(Probe.IsTableEmpty(56680), 'Empty table must be empty');
    end;

    [Test]
    procedure IsEmptyOnNonEmptyTable()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        R.Id := 1; R.Insert();
        Assert.IsFalse(Probe.IsTableEmpty(56680), 'Table with row must not be empty');
    end;

    // --- Delete ---

    [Test]
    procedure DeleteViaRecRef()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        // [GIVEN] 2 rows
        R.Id := 1; R.Name := 'Keep'; R.Insert();
        R.Id := 2; R.Name := 'Remove'; R.Insert();

        // [WHEN] Delete row with Id=2 via RecordRef
        Assert.IsTrue(Probe.DeleteFirstById(56680, 2), 'Delete must succeed');

        // [THEN] Only 1 row remains
        Assert.AreEqual(1, Probe.CountRecords(56680), 'One row must remain');
    end;

    [Test]
    procedure DeleteAllViaRecRef()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        R.Id := 1; R.Insert();
        R.Id := 2; R.Insert();
        R.Id := 3; R.Insert();

        Probe.DeleteAll(56680);

        Assert.AreEqual(0, Probe.CountRecords(56680), 'All rows must be deleted');
    end;

    // --- Modify ---

    [Test]
    procedure ModifyViaRecRef()
    var
        Probe: Codeunit "RF Probe";
    begin
        // [GIVEN] Insert with name 'Old', then modify to 'New' via RecRef
        // [THEN] Read back returns 'New'
        Assert.AreEqual('New', Probe.InsertAndModify(56680, 99, 'Old', 'New'), 'Modify must update field');
    end;

    // --- Filtered count (SetRange on FieldRef) ---

    [Test]
    procedure SetRangeOnFieldRefFilters()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        R.Id := 1; R.Insert();
        R.Id := 2; R.Insert();
        R.Id := 3; R.Insert();

        // [WHEN] SetRange on field 1 with value 2
        // [THEN] Count returns 1
        Assert.AreEqual(1, Probe.FilteredCount(56680, 1, 2), 'SetRange must filter to 1 row');
    end;

    // --- Reset clears filters ---

    [Test]
    procedure ResetClearsFilters()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        R.Id := 1; R.Insert();
        R.Id := 2; R.Insert();

        // [WHEN] SetRange then Reset
        // [THEN] Count returns all rows
        Assert.AreEqual(2, Probe.ResetAndCount(56680, 1, 1), 'Reset must clear filters');
    end;

    // --- Negative tests ---

    [Test]
    procedure InsertDuplicateViaRecRefFails()
    var
        Probe: Codeunit "RF Probe";
        R: Record "RF Test Item";
    begin
        // [GIVEN] A row with Id=1 exists
        R.Id := 1; R.Name := 'First'; R.Insert();

        // [WHEN] Insert duplicate via RecRef
        // [THEN] Error is thrown
        asserterror Probe.SetFieldAndInsert(56680, 1, 1, 2, 'Duplicate');
        Assert.ExpectedError('already exists');
    end;
}
