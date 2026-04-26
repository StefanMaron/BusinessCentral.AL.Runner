codeunit 57802 "FA FieldRef Active Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "FA Helper";

    // -----------------------------------------------------------------------
    // FieldRef.Active() — normal fields always return true
    // -----------------------------------------------------------------------

    [Test]
    procedure Active_PKField_ReturnsTrue()
    var
        RecRef: RecordRef;
    begin
        // [GIVEN] RecordRef opened on FA Test
        // [WHEN] Active() called on field(1) — the PK field (Code)
        // [THEN] returns true (PK fields are always active)
        RecRef.Open(Database::"FA Test");
        Assert.IsTrue(Helper.IsFieldActiveByNo(RecRef, 1), 'PK field (Code) must be active');
        RecRef.Close();
    end;

    [Test]
    procedure Active_TextNonPKField_ReturnsTrue()
    var
        RecRef: RecordRef;
    begin
        // [GIVEN] RecordRef opened on FA Test
        // [WHEN] Active() called on field(2) — Name (Text)
        // [THEN] returns true
        RecRef.Open(Database::"FA Test");
        Assert.IsTrue(Helper.IsFieldActiveByNo(RecRef, 2), 'Name field (Text) must be active');
        RecRef.Close();
    end;

    [Test]
    procedure Active_DecimalField_ReturnsTrue()
    var
        RecRef: RecordRef;
    begin
        // [GIVEN] RecordRef opened on FA Test
        // [WHEN] Active() called on field(3) — Amount (Decimal)
        // [THEN] returns true
        RecRef.Open(Database::"FA Test");
        Assert.IsTrue(Helper.IsFieldActiveByNo(RecRef, 3), 'Amount field (Decimal) must be active');
        RecRef.Close();
    end;

    [Test]
    procedure Active_BooleanField_ReturnsTrue()
    var
        RecRef: RecordRef;
    begin
        // [GIVEN] RecordRef opened on FA Test
        // [WHEN] Active() called on field(5) — Active (Boolean)
        // [THEN] returns true
        RecRef.Open(Database::"FA Test");
        Assert.IsTrue(Helper.IsFieldActiveByNo(RecRef, 5), 'Active field (Boolean) must be active');
        RecRef.Close();
    end;

    [Test]
    procedure Active_AllFieldsActiveViaFieldIndex()
    var
        RecRef: RecordRef;
        Count: Integer;
    begin
        // [GIVEN] RecordRef opened on FA Test (5 fields)
        // [WHEN] CountActiveFields iterates all fields via FieldIndex and checks Active()
        // [THEN] all 5 fields are active
        RecRef.Open(Database::"FA Test");
        Count := Helper.CountActiveFields(RecRef);
        Assert.AreEqual(5, Count, 'All 5 fields must be active (normal fields are always active)');
        RecRef.Close();
    end;

    [Test]
    procedure Active_ActiveCountMatchesFieldCount()
    var
        RecRef: RecordRef;
        ActiveCount: Integer;
        FieldCount: Integer;
    begin
        // [GIVEN] RecordRef opened on FA Test
        // [WHEN] Active() checked on all fields
        // [THEN] active count equals FieldCount — no fields are inactive
        RecRef.Open(Database::"FA Test");
        FieldCount := RecRef.FieldCount();
        ActiveCount := Helper.CountActiveFields(RecRef);
        Assert.AreEqual(FieldCount, ActiveCount,
            'Active field count must equal total FieldCount for a table with no disabled fields');
        RecRef.Close();
    end;
}
