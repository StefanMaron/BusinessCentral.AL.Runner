codeunit 55401 "FieldGroup Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // FieldGroups — positive tests (CRUD still works on tables with FieldGroups)
    // -----------------------------------------------------------------------

    [Test]
    procedure InsertAndGetRecordOnTableWithFieldGroups()
    var
        Rec: Record "FieldGroup Test Table";
    begin
        // [GIVEN] A table that declares FieldGroups (DropDown, Brick)
        // [WHEN] A record is inserted and retrieved by PK
        Rec.Init();
        Rec.Code := 'FG001';
        Rec.Name := 'Test Record';
        Rec.Amount := 99.50;
        Rec.Insert();

        // [THEN] Get succeeds and fields are intact
        Rec.Get('FG001');
        Assert.AreEqual('Test Record', Rec.Name, 'Name should survive round-trip on FieldGroup table');
        Assert.AreEqual(99.50, Rec.Amount, 'Amount should survive round-trip on FieldGroup table');
    end;

    [Test]
    procedure ModifyRecordOnTableWithFieldGroups()
    var
        Rec: Record "FieldGroup Test Table";
    begin
        // [GIVEN] An inserted record on a table with FieldGroups
        Rec.Init();
        Rec.Code := 'FG002';
        Rec.Name := 'Original';
        Rec.Insert();

        // [WHEN] The record is modified
        Rec.Name := 'Updated';
        Rec.Modify();

        // [THEN] The new value is persisted
        Rec.Get('FG002');
        Assert.AreEqual('Updated', Rec.Name, 'Modified name should be persisted');
    end;

    [Test]
    procedure DeleteRecordOnTableWithFieldGroups()
    var
        Rec: Record "FieldGroup Test Table";
    begin
        // [GIVEN] An inserted record on a table with FieldGroups
        Rec.Init();
        Rec.Code := 'FG003';
        Rec.Name := 'ToDelete';
        Rec.Insert();

        // [WHEN] The record is deleted
        Rec.Delete();

        // [THEN] Get returns false (record no longer exists)
        Assert.IsFalse(Rec.Get('FG003'), 'Deleted record should not be found');
    end;

    [Test]
    procedure FindSetOnTableWithFieldGroups()
    var
        Rec: Record "FieldGroup Test Table";
        Count: Integer;
    begin
        // [GIVEN] Multiple records on a table with FieldGroups
        InsertFGRecord('FA001', 'Alpha', 10);
        InsertFGRecord('FA002', 'Beta', 20);
        InsertFGRecord('FA003', 'Gamma', 30);

        // [WHEN] FindSet iterates all records
        Rec.FindSet();
        repeat
            Count += 1;
        until Rec.Next() = 0;

        // [THEN] All three records are visited
        Assert.AreEqual(3, Count, 'FindSet should visit all 3 records on FieldGroup table');
    end;

    [Test]
    procedure CountOnTableWithFieldGroups()
    var
        Rec: Record "FieldGroup Test Table";
    begin
        // [GIVEN] Two records on a table with FieldGroups
        InsertFGRecord('FC001', 'One', 1);
        InsertFGRecord('FC002', 'Two', 2);

        // [THEN] Count returns the correct number
        Assert.AreEqual(2, Rec.Count(), 'Count should return 2 for FieldGroup table');
    end;

    // -----------------------------------------------------------------------
    // FieldGroups — negative tests
    // -----------------------------------------------------------------------

    [Test]
    procedure GetNonExistentRecordOnTableWithFieldGroups()
    var
        Rec: Record "FieldGroup Test Table";
    begin
        // [GIVEN] No record exists with key 'MISSING'
        // [THEN] Get returns false — FieldGroups do not affect key lookups
        Assert.IsFalse(Rec.Get('MISSING'), 'Get on non-existent key should return false');
    end;

    [Test]
    procedure InsertDuplicateKeyOnTableWithFieldGroups()
    var
        Rec: Record "FieldGroup Test Table";
    begin
        // [GIVEN] A record with key 'DUP001' already exists
        InsertFGRecord('DUP001', 'First', 1);

        // [WHEN/THEN] Inserting a duplicate key raises an error
        Rec.Init();
        Rec.Code := 'DUP001';
        Rec.Name := 'Duplicate';
        asserterror Rec.Insert();
        Assert.ExpectedError('already exists');
    end;

    local procedure InsertFGRecord(Code: Code[20]; Name: Text[100]; Amount: Decimal)
    var
        Rec: Record "FieldGroup Test Table";
    begin
        Rec.Init();
        Rec.Code := Code;
        Rec.Name := Name;
        Rec.Amount := Amount;
        Rec.Insert();
    end;
}
