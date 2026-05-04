// Renumbered from 59401 to avoid collision in new bucket layout (#1385).
codeunit 1059401 "DataError Suppress Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // INSERT: capturing return value should suppress the error
    // -----------------------------------------------------------------------

    [Test]
    procedure InsertDuplicateWithReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] A record already exists
        R."Entry No." := 1;
        R.Description := 'First';
        R.Insert();

        // [WHEN] Inserting a duplicate and capturing the return value
        R.Description := 'Duplicate';
        Ok := R.Insert();

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Insert of duplicate PK should return false when return captured');
    end;

    [Test]
    procedure InsertDuplicateWithoutReturnThrows()
    var
        R: Record "DataError Probe";
        Second: Record "DataError Probe";
    begin
        // [GIVEN] A record already exists
        R."Entry No." := 1;
        R.Insert();

        // [WHEN] Inserting a duplicate without capturing the return value
        asserterror begin
            Second."Entry No." := 1;
            Second.Insert();
        end;

        // [THEN] Should have thrown an error
        Assert.ExpectedError('already exists');
    end;

    [Test]
    procedure InsertUniqueWithReturnReturnsTrue()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [WHEN] Inserting a unique record and capturing the return value
        R."Entry No." := 1;
        Ok := R.Insert();

        // [THEN] Should return true
        Assert.IsTrue(Ok, 'Insert of unique record should return true');
    end;

    // -----------------------------------------------------------------------
    // DELETE: not capturing return value should throw when record missing
    // -----------------------------------------------------------------------

    [Test]
    procedure DeleteMissingWithReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] Deleting a non-existing record and capturing the return value
        R."Entry No." := 999;
        Ok := R.Delete();

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Delete of missing record should return false when return captured');
    end;

    [Test]
    procedure DeleteMissingWithoutReturnThrows()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] Deleting a non-existing record without capturing the return value
        R."Entry No." := 999;
        asserterror R.Delete();

        // [THEN] Should have thrown an error
        Assert.ExpectedError('does not exist');
    end;

    [Test]
    procedure DeleteExistingReturnsTrue()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] A record exists
        R."Entry No." := 1;
        R.Insert();

        // [WHEN] Deleting the existing record
        Ok := R.Delete();

        // [THEN] Should return true
        Assert.IsTrue(Ok, 'Delete of existing record should return true');
    end;

    // -----------------------------------------------------------------------
    // MODIFY: verify existing errorLevel handling is correct
    // -----------------------------------------------------------------------

    [Test]
    procedure ModifyMissingWithReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] Modifying a non-existing record and capturing the return value
        R."Entry No." := 999;
        Ok := R.Modify();

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Modify of missing record should return false when return captured');
    end;

    [Test]
    procedure ModifyMissingWithoutReturnThrows()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] Modifying a non-existing record without capturing the return value
        R."Entry No." := 999;
        asserterror R.Modify();

        // [THEN] Should have thrown an error
        Assert.ExpectedError('does not exist');
    end;

    // -----------------------------------------------------------------------
    // GET: verify existing errorLevel handling is correct
    // -----------------------------------------------------------------------

    [Test]
    procedure GetMissingWithReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] Getting a non-existing record and capturing the return value
        Ok := R.Get(999);

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Get of missing record should return false when return captured');
    end;

    [Test]
    procedure GetMissingWithoutReturnThrows()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] Getting a non-existing record without capturing the return value
        asserterror R.Get(999);

        // [THEN] Should have thrown an error
        Assert.ExpectedError('does not exist');
    end;

    // -----------------------------------------------------------------------
    // FINDFIRST: verify existing errorLevel handling is correct
    // -----------------------------------------------------------------------

    [Test]
    procedure FindFirstEmptyWithReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] Finding first on empty table and capturing return
        Ok := R.FindFirst();

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'FindFirst on empty table should return false when return captured');
    end;

    [Test]
    procedure FindFirstEmptyWithoutReturnThrows()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] Finding first on empty table without capturing return
        asserterror R.FindFirst();

        // [THEN] Should have thrown an error
        Assert.ExpectedError('No records in table');
    end;

    // -----------------------------------------------------------------------
    // FINDLAST: verify existing errorLevel handling is correct
    // -----------------------------------------------------------------------

    [Test]
    procedure FindLastEmptyWithReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] Finding last on empty table and capturing return
        Ok := R.FindLast();

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'FindLast on empty table should return false when return captured');
    end;

    [Test]
    procedure FindLastEmptyWithoutReturnThrows()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] Finding last on empty table without capturing return
        asserterror R.FindLast();

        // [THEN] Should have thrown an error
        Assert.ExpectedError('No records in table');
    end;

    // -----------------------------------------------------------------------
    // FINDSET: verify existing errorLevel handling is correct
    // -----------------------------------------------------------------------

    [Test]
    procedure FindSetEmptyWithReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] FindSet on empty table and capturing return
        Ok := R.FindSet();

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'FindSet on empty table should return false when return captured');
    end;

    [Test]
    procedure FindSetEmptyWithoutReturnThrows()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] FindSet on empty table without capturing return
        asserterror R.FindSet();

        // [THEN] Should have thrown an error
        Assert.ExpectedError('No records in table');
    end;

    // -----------------------------------------------------------------------
    // Insert-or-Modify pattern (real-world pattern from issue)
    // -----------------------------------------------------------------------

    [Test]
    procedure InsertOrModifyPattern()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] First insert works
        R."Entry No." := 1;
        R.Description := 'Original';
        R.Insert();

        // [WHEN] Using the defensive "if not Insert() then Modify()" pattern
        R.Description := 'Updated';
        if not R.Insert() then
            R.Modify();

        // [THEN] Record should have the updated description
        R.Get(1);
        Assert.AreEqual('Updated', R.Description, 'Insert-or-Modify should have updated the description');
    end;

    // -----------------------------------------------------------------------
    // RunTrigger overload coverage (Insert(true)/Delete(true))
    // -----------------------------------------------------------------------

    [Test]
    procedure InsertDuplicateWithRunTriggerAndReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] A record already exists
        R."Entry No." := 1;
        R.Insert(true);

        // [WHEN] Inserting a duplicate with RunTrigger=true and capturing return
        R.Description := 'Dup';
        Ok := R.Insert(true);

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Insert(true) of duplicate PK should return false when return captured');
    end;

    [Test]
    procedure InsertDuplicateWithRunTriggerWithoutReturnThrows()
    var
        R: Record "DataError Probe";
        Second: Record "DataError Probe";
    begin
        // [GIVEN] A record already exists
        R."Entry No." := 1;
        R.Insert(true);

        // [WHEN] Inserting a duplicate with RunTrigger=true without capturing return
        asserterror begin
            Second."Entry No." := 1;
            Second.Insert(true);
        end;

        // [THEN] Should have thrown an error
        Assert.ExpectedError('already exists');
    end;

    [Test]
    procedure DeleteMissingWithRunTriggerAndReturnDoesNotThrow()
    var
        R: Record "DataError Probe";
        Ok: Boolean;
    begin
        // [GIVEN] No records exist
        // [WHEN] Deleting a non-existing record with RunTrigger=true and capturing return
        R."Entry No." := 999;
        Ok := R.Delete(true);

        // [THEN] Should return false, not throw
        Assert.IsFalse(Ok, 'Delete(true) of missing record should return false when return captured');
    end;

    [Test]
    procedure DeleteMissingWithRunTriggerWithoutReturnThrows()
    var
        R: Record "DataError Probe";
    begin
        // [GIVEN] No records exist
        // [WHEN] Deleting a non-existing record with RunTrigger=true without capturing return
        R."Entry No." := 999;
        asserterror R.Delete(true);

        // [THEN] Should have thrown an error
        Assert.ExpectedError('does not exist');
    end;
}
