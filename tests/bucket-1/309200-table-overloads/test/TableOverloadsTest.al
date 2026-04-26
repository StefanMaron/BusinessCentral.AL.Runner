codeunit 309202 "Table Overloads Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Insert(Boolean) — trigger runs when RunTrigger = true
    // -----------------------------------------------------------------------

    [Test]
    procedure Insert_Boolean_TriggerTrue_CounterIncremented()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
    begin
        // [GIVEN] A new record
        Rec.Init();
        Rec.Id := 1;
        Rec.Counter := 0;

        // [WHEN] Insert(true) — trigger should fire and increment Counter
        Helper.InsertWithTriggerFlag(Rec, true);

        // [THEN] OnInsert trigger incremented Counter
        Rec.Get(1);
        Assert.AreEqual(1, Rec.Counter, 'Counter should be 1 after Insert(true) fires OnInsert');
    end;

    [Test]
    procedure Insert_Boolean_TriggerFalse_CounterNotIncremented()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
    begin
        // [GIVEN] A new record
        Rec.Init();
        Rec.Id := 2;
        Rec.Counter := 0;

        // [WHEN] Insert(false) — trigger should NOT fire
        Helper.InsertWithTriggerFlag(Rec, false);

        // [THEN] Counter stays at 0 because OnInsert was not triggered
        Rec.Get(2);
        Assert.AreEqual(0, Rec.Counter, 'Counter should remain 0 after Insert(false) skips OnInsert');
    end;

    // -----------------------------------------------------------------------
    // Insert(Boolean, Boolean) — compiles and inserts correctly
    // -----------------------------------------------------------------------

    [Test]
    procedure Insert_BoolBool_TriggerTrue_Inserts()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
    begin
        // [GIVEN] A new record
        Rec.Init();
        Rec.Id := 3;
        Rec.Counter := 0;

        // [WHEN] Insert(true, false) — runTrigger=true, belowXRec=false
        Helper.InsertWithTriggerAndBelowXRec(Rec, true, false);

        // [THEN] Record exists and OnInsert triggered counter
        Rec.Get(3);
        Assert.AreEqual(1, Rec.Counter, 'Counter should be 1 after Insert(true,false) fires OnInsert');
    end;

    [Test]
    procedure Insert_BoolBool_TriggerFalse_InsertsWithoutTrigger()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
    begin
        // [GIVEN] A new record
        Rec.Init();
        Rec.Id := 4;
        Rec.Counter := 0;

        // [WHEN] Insert(false, false) — skip trigger
        Helper.InsertWithTriggerAndBelowXRec(Rec, false, false);

        // [THEN] Record exists but counter not incremented
        Rec.Get(4);
        Assert.AreEqual(0, Rec.Counter, 'Counter should be 0 after Insert(false,false) skips OnInsert');
    end;

    // -----------------------------------------------------------------------
    // FindSet(Boolean, Boolean) — returns records correctly
    // -----------------------------------------------------------------------

    [Test]
    procedure FindSet_BoolBool_FindsExistingRecords()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
        Found: Boolean;
    begin
        // [GIVEN] A record exists
        Rec.Init();
        Rec.Id := 10;
        Rec.Name := 'FindSet Test';
        Rec.Insert(false);

        // [WHEN] FindSet(false, false) — forUpdate=false, updateKey=false
        Rec.Reset();
        Rec.SetRange(Id, 10);
        Found := Helper.FindSetForUpdateAndKey(Rec, false, false);

        // [THEN] Record is found
        Assert.IsTrue(Found, 'FindSet(false,false) should find existing record');
        Assert.AreEqual('FindSet Test', Rec.Name, 'FindSet should position on correct record');
    end;

    [Test]
    procedure FindSet_BoolBool_ReturnsFalseWhenEmpty()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
        Found: Boolean;
    begin
        // [GIVEN] No records matching Id = 999
        Rec.Reset();
        Rec.SetRange(Id, 999);

        // [WHEN] FindSet(false, false)
        Found := Helper.FindSetForUpdateAndKey(Rec, false, false);

        // [THEN] Returns false
        Assert.IsFalse(Found, 'FindSet(false,false) should return false when no records match');
    end;

    // -----------------------------------------------------------------------
    // TransferFields(Table, Boolean, Boolean) — 3-arg overload
    // -----------------------------------------------------------------------

    [Test]
    procedure TransferFields_ThreeArgs_CopiesFields()
    var
        Source: Record "Table Overloads";
        Target: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
    begin
        // [GIVEN] Source record with values
        Source.Init();
        Source.Id := 20;
        Source.Name := 'Transfer Test';
        Source.Counter := 42;
        Source.Insert(false);

        // Target starts empty
        Target.Init();
        Target.Id := 21;

        // [WHEN] TransferFields(Source, true, false) — initPK=true, initSystem=false
        Helper.TransferFieldsThreeArgs(Target, Source, true, false);

        // [THEN] Name and Counter are transferred (PK too since initPK=true)
        Assert.AreEqual('Transfer Test', Target.Name, 'Name should be transferred by 3-arg TransferFields');
        Assert.AreEqual(42, Target.Counter, 'Counter should be transferred by 3-arg TransferFields');
    end;

    [Test]
    procedure TransferFields_ThreeArgs_InitPKFalse_PreservesTargetPK()
    var
        Source: Record "Table Overloads";
        Target: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
    begin
        // [GIVEN] Source with Id=30, Target with Id=99
        Source.Init();
        Source.Id := 30;
        Source.Name := 'Source Name';
        Source.Insert(false);

        Target.Init();
        Target.Id := 99;

        // [WHEN] TransferFields(Source, false, false) — skip PK copy
        Helper.TransferFieldsThreeArgs(Target, Source, false, false);

        // [THEN] Target's PK (Id=99) is preserved; Name is still transferred
        Assert.AreEqual(99, Target.Id, 'Target PK should be preserved when initPK=false');
        Assert.AreEqual('Source Name', Target.Name, 'Name should still be transferred when initPK=false');
    end;

    // -----------------------------------------------------------------------
    // FullyQualifiedName() — returns non-empty string with company and table
    // -----------------------------------------------------------------------

    [Test]
    procedure FullyQualifiedName_ReturnsNonEmptyString()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
        Fqn: Text;
    begin
        // [WHEN] FullyQualifiedName() is called
        Fqn := Helper.GetFullyQualifiedName(Rec);

        // [THEN] Result is non-empty
        Assert.AreNotEqual('', Fqn, 'FullyQualifiedName should return a non-empty string');
    end;

    [Test]
    procedure FullyQualifiedName_ContainsDollarSeparator()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
        Fqn: Text;
    begin
        // [WHEN] FullyQualifiedName() is called
        Fqn := Helper.GetFullyQualifiedName(Rec);

        // [THEN] Result contains '$' separating company from table name
        Assert.IsTrue(Fqn.Contains('$'), 'FullyQualifiedName should contain "$" separator');
    end;

    [Test]
    procedure FullyQualifiedName_ContainsTableName()
    var
        Rec: Record "Table Overloads";
        Helper: Codeunit "Table Overloads Helper";
        Fqn: Text;
    begin
        // [WHEN] FullyQualifiedName() is called
        Fqn := Helper.GetFullyQualifiedName(Rec);

        // [THEN] Result contains the table name
        Assert.IsTrue(Fqn.Contains('Table Overloads'), 'FullyQualifiedName should contain the table name');
    end;
}
