codeunit 95102 "Rename Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Positive: Basic rename updates the table row
    // -----------------------------------------------------------------------

    [Test]
    procedure RenameUpdatesTableRow()
    var
        R: Record "Rename Probe";
        Lookup: Record "Rename Probe";
    begin
        // [GIVEN] A record exists with key 1
        R."Entry No." := 1;
        R.Description := 'Original';
        R.Amount := 100.50;
        R.Insert();

        // [WHEN] Renaming to key 2
        R.Get(1);
        R.Rename(2);

        // [THEN] Get by new key succeeds and data is preserved
        Lookup.Get(2);
        Assert.AreEqual('Original', Lookup.Description, 'Description should be preserved after rename');
        Assert.AreEqual(100.50, Lookup.Amount, 'Amount should be preserved after rename');
    end;

    [Test]
    procedure RenameRemovesOldKey()
    var
        R: Record "Rename Probe";
        Lookup: Record "Rename Probe";
        Found: Boolean;
    begin
        // [GIVEN] A record exists with key 1
        R."Entry No." := 1;
        R.Description := 'ToMove';
        R.Insert();

        // [WHEN] Renaming to key 99
        R.Get(1);
        R.Rename(99);

        // [THEN] Get by OLD key should fail
        Found := Lookup.Get(1);
        Assert.IsFalse(Found, 'Old key should no longer exist after rename');
    end;

    // -----------------------------------------------------------------------
    // Negative: Rename non-existent record throws
    // -----------------------------------------------------------------------

    [Test]
    procedure RenameNonExistentThrows()
    var
        R: Record "Rename Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] Trying to rename a record that doesn't exist in the table
        R."Entry No." := 42;

        asserterror R.Rename(99);

        // [THEN] Error should mention the record was not found
        Assert.ExpectedError('does not exist');
    end;

    // -----------------------------------------------------------------------
    // Negative: Rename to conflicting key throws
    // -----------------------------------------------------------------------

    [Test]
    procedure RenameToExistingKeyThrows()
    var
        R: Record "Rename Probe";
        Blocker: Record "Rename Probe";
    begin
        // [GIVEN] Two records exist
        R."Entry No." := 1;
        R.Description := 'First';
        R.Insert();
        Blocker."Entry No." := 2;
        Blocker.Description := 'Second';
        Blocker.Insert();

        // [WHEN] Renaming record 1 to key 2 (already taken)
        R.Get(1);
        asserterror R.Rename(2);

        // [THEN] Error should mention the record already exists
        Assert.ExpectedError('already exists');
    end;

    // -----------------------------------------------------------------------
    // Error suppression: capture return value
    // -----------------------------------------------------------------------

    [Test]
    procedure RenameNonExistentReturnsFalse()
    var
        R: Record "Rename Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        R."Entry No." := 42;

        // [WHEN] Renaming with return value captured
        Ok := R.Rename(99);

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Rename of non-existent record should return false');
    end;

    [Test]
    procedure RenameToConflictReturnsFalse()
    var
        R: Record "Rename Probe";
        Blocker: Record "Rename Probe";
        Ok: Boolean;
    begin
        // [GIVEN] Two records exist
        R."Entry No." := 1;
        R.Insert();
        Blocker."Entry No." := 2;
        Blocker.Insert();

        // [WHEN] Renaming record 1 to key 2 with return value captured
        R.Get(1);
        Ok := R.Rename(2);

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Rename to conflicting key should return false');
    end;

    // -----------------------------------------------------------------------
    // Composite primary key rename
    // -----------------------------------------------------------------------

    [Test]
    procedure RenameCompositeKeyUpdatesRow()
    var
        R: Record "Rename Composite";
        Lookup: Record "Rename Composite";
    begin
        // [GIVEN] A record with composite key (1, 'ORD001', 10000)
        R."Doc Type" := 1;
        R."Doc No." := 'ORD001';
        R."Line No." := 10000;
        R.Description := 'Line item';
        R.Insert();

        // [WHEN] Renaming to (2, 'ORD002', 20000)
        R.Get(1, 'ORD001', 10000);
        R.Rename(2, 'ORD002', 20000);

        // [THEN] New key exists with correct data
        Lookup.Get(2, 'ORD002', 20000);
        Assert.AreEqual('Line item', Lookup.Description, 'Description should survive composite rename');
    end;

    [Test]
    procedure RenameCompositeKeyRemovesOld()
    var
        R: Record "Rename Composite";
        Lookup: Record "Rename Composite";
        Found: Boolean;
    begin
        // [GIVEN] A record with composite key
        R."Doc Type" := 1;
        R."Doc No." := 'ORD001';
        R."Line No." := 10000;
        R.Description := 'Original line';
        R.Insert();

        // [WHEN] Renaming to a new composite key
        R.Get(1, 'ORD001', 10000);
        R.Rename(1, 'ORD001', 20000);

        // [THEN] Old key should not exist
        Found := Lookup.Get(1, 'ORD001', 10000);
        Assert.IsFalse(Found, 'Old composite key should no longer exist after rename');
    end;

    // -----------------------------------------------------------------------
    // Positive: Rename preserves total record count
    // -----------------------------------------------------------------------

    [Test]
    procedure RenameDoesNotChangeRecordCount()
    var
        R: Record "Rename Probe";
    begin
        // [GIVEN] Three records
        R."Entry No." := 1; R.Insert();
        R.Init();
        R."Entry No." := 2; R.Insert();
        R.Init();
        R."Entry No." := 3; R.Insert();

        // [WHEN] Renaming record 2 to key 20
        R.Get(2);
        R.Rename(20);

        // [THEN] Count should still be 3
        R.Reset();
        Assert.AreEqual(3, R.Count(), 'Rename should not change total record count');
    end;
}
