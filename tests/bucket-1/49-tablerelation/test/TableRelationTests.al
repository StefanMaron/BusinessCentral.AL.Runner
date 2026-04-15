codeunit 55502 "TableRelation Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // TableRelation syntax — positive tests
    // -----------------------------------------------------------------------

    [Test]
    procedure TableRelation_TableCompilesAndInsertWorks()
    var
        Child: Record "TR Child";
    begin
        // [GIVEN/WHEN] Insert a child record with a Parent Code value
        // (no parent record exists — runner does not enforce FK relations)
        Child.Init();
        Child."Entry No." := 1;
        Child."Parent Code" := 'ACME';
        Child.Amount := 100;
        Child.Insert();

        // [THEN] Record is inserted without error and can be retrieved
        Child.Get(1);
        Assert.AreEqual('ACME', Child."Parent Code", 'Parent Code should be stored as-is');
        Assert.AreEqual(100, Child.Amount, 'Amount should be stored correctly');
    end;

    [Test]
    procedure TableRelation_InsertWithExistingParentWorks()
    var
        Parent: Record "TR Parent";
        Child: Record "TR Child";
    begin
        // [GIVEN] A parent record exists
        Parent.Init();
        Parent.Code := 'BETA';
        Parent.Description := 'Beta Corp';
        Parent.Insert();

        // [WHEN] A child record references the parent
        Child.Init();
        Child."Entry No." := 2;
        Child."Parent Code" := 'BETA';
        Child.Amount := 200;
        Child.Insert();

        // [THEN] Both records readable; child holds the parent code
        Child.Get(2);
        Assert.AreEqual('BETA', Child."Parent Code", 'Child should reference BETA');
        Parent.Get('BETA');
        Assert.AreEqual('Beta Corp', Parent.Description, 'Parent description intact');
    end;

    [Test]
    procedure TableRelation_ModifyWorks()
    var
        Child: Record "TR Child";
    begin
        // [GIVEN] An existing child record
        Child.Init();
        Child."Entry No." := 3;
        Child."Parent Code" := 'OLD';
        Child.Amount := 50;
        Child.Insert();

        // [WHEN] Modify updates the Parent Code and Amount
        Child.Get(3);
        Child."Parent Code" := 'NEW';
        Child.Amount := 75;
        Child.Modify();

        // [THEN] Updated values are persisted
        Child.Get(3);
        Assert.AreEqual('NEW', Child."Parent Code", 'Modified Parent Code should be NEW');
        Assert.AreEqual(75, Child.Amount, 'Modified Amount should be 75');
    end;

    [Test]
    procedure TableRelation_DeleteWorks()
    var
        Child: Record "TR Child";
    begin
        // [GIVEN] An existing child record
        Child.Init();
        Child."Entry No." := 4;
        Child."Parent Code" := 'DEL';
        Child.Amount := 10;
        Child.Insert();

        // [WHEN] Delete the record
        Child.Get(4);
        Child.Delete();

        // [THEN] Record no longer exists
        Assert.IsFalse(Child.Get(4), 'Deleted record should not be found');
    end;

    [Test]
    procedure TableRelation_CountReflectsInsertedRecords()
    var
        Child: Record "TR Child";
    begin
        // [GIVEN] Two child records inserted (Entry No. 10 and 11)
        Child.Init();
        Child."Entry No." := 10;
        Child."Parent Code" := 'X';
        Child.Insert();

        Child.Init();
        Child."Entry No." := 11;
        Child."Parent Code" := 'Y';
        Child.Insert();

        // [WHEN/THEN] Count should reflect inserted records
        Child.SetFilter("Entry No.", '10|11');
        Assert.AreEqual(2, Child.Count, 'Count should return 2 for the two inserted records');
    end;

    // -----------------------------------------------------------------------
    // TableRelation — negative test: no FK enforcement
    // -----------------------------------------------------------------------

    [Test]
    procedure TableRelation_InsertWithoutParentDoesNotError()
    var
        Child: Record "TR Child";
        Parent: Record "TR Parent";
        Inserted: Boolean;
    begin
        // [GIVEN] No parent record with code 'GHOST' exists
        Assert.IsFalse(Parent.Get('GHOST'), 'Parent GHOST must not exist before test');

        // [WHEN] Insert a child referencing the non-existent parent
        Child.Init();
        Child."Entry No." := 99;
        Child."Parent Code" := 'GHOST';
        Child.Insert();  // must NOT raise an error — runner does not enforce FK

        // [THEN] Record was inserted successfully
        Assert.IsTrue(Child.Get(99), 'Child with orphan Parent Code should be inserted without error');
        Assert.AreEqual('GHOST', Child."Parent Code", 'Orphan Parent Code stored correctly');
    end;
}
