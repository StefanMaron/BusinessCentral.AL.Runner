codeunit 1218002 "CKI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "CKI Src";

    local procedure SeedRecords()
    var
        Rec: Record "CKI Table";
    begin
        Rec.DeleteAll();

        Rec.Init();
        Rec.Id := 1; Rec.Name := 'Alpha'; Rec.SortVal := 30;
        Rec.Insert();

        Rec.Init();
        Rec.Id := 2; Rec.Name := 'Bravo'; Rec.SortVal := 10;
        Rec.Insert();

        Rec.Init();
        Rec.Id := 3; Rec.Name := 'Charlie'; Rec.SortVal := 20;
        Rec.Insert();
    end;

    // ------------------------------------------------------------------
    // Positive: setting CurrentKeyIndex re-sorts the record set by the
    // secondary key. Iteration order must match the SortVal order, not
    // the insertion (PK) order.
    // ------------------------------------------------------------------
    [Test]
    procedure RecordRef_CurrentKeyIndex_SwitchesSortToSecondaryKey()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        IdsInOrder: Text;
    begin
        SeedRecords();

        RecRef.Open(Database::"CKI Table");
        Src.SetCurrentKeyIndex(RecRef, 2);

        Assert.AreEqual(2, Src.GetCurrentKeyIndex(RecRef),
            'CurrentKeyIndex getter should return the value that was set');

        if RecRef.FindSet() then
            repeat
                FldRef := RecRef.Field(1);
                IdsInOrder += Format(FldRef.Value) + ',';
            until RecRef.Next() = 0;

        // Secondary key is SortVal: 10 (id=2), 20 (id=3), 30 (id=1)
        Assert.AreEqual('2,3,1,', IdsInOrder,
            'Iteration order after CurrentKeyIndex := 2 must follow SortVal ascending');
    end;

    // ------------------------------------------------------------------
    // Positive: KeyCount reflects both declared keys.
    // ------------------------------------------------------------------
    [Test]
    procedure RecordRef_KeyCount_ReturnsDeclaredKeyCount()
    var
        RecRef: RecordRef;
    begin
        SeedRecords();
        RecRef.Open(Database::"CKI Table");
        Assert.AreEqual(2, Src.GetKeyCount(RecRef),
            'KeyCount should match the number of declared keys (PK + BySortVal)');
    end;

    // ------------------------------------------------------------------
    // Positive: default CurrentKeyIndex is 1 (primary key) and iteration
    // follows PK order.
    // ------------------------------------------------------------------
    [Test]
    procedure RecordRef_CurrentKeyIndex_DefaultsToPrimaryKey()
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        IdsInOrder: Text;
    begin
        SeedRecords();

        RecRef.Open(Database::"CKI Table");
        Assert.AreEqual(1, Src.GetCurrentKeyIndex(RecRef),
            'Default CurrentKeyIndex should be 1 (primary key)');

        if RecRef.FindSet() then
            repeat
                FldRef := RecRef.Field(1);
                IdsInOrder += Format(FldRef.Value) + ',';
            until RecRef.Next() = 0;

        Assert.AreEqual('1,2,3,', IdsInOrder,
            'Default iteration must follow PK (Id) ascending order');
    end;

    // ------------------------------------------------------------------
    // Negative: setting CurrentKeyIndex to an out-of-range value must
    // throw with a recognizable error message.
    // ------------------------------------------------------------------
    [Test]
    procedure RecordRef_CurrentKeyIndex_OutOfRange_Throws()
    var
        RecRef: RecordRef;
    begin
        SeedRecords();
        RecRef.Open(Database::"CKI Table");

        asserterror Src.SetCurrentKeyIndex(RecRef, 99);
        Assert.ExpectedError('out of range');
    end;

    // ------------------------------------------------------------------
    // Negative: zero is not a valid key index (keys are 1-based).
    // ------------------------------------------------------------------
    [Test]
    procedure RecordRef_CurrentKeyIndex_Zero_Throws()
    var
        RecRef: RecordRef;
    begin
        SeedRecords();
        RecRef.Open(Database::"CKI Table");

        asserterror Src.SetCurrentKeyIndex(RecRef, 0);
        Assert.ExpectedError('out of range');
    end;
}
