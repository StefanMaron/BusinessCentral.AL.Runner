// Issue 1275: CS1503 when MockRecordHandle is passed where MockRecordRef is expected.
// BC can emit code paths where a Record variable (MockRecordHandle) is used where
// a RecordRef (MockRecordRef) parameter is expected without an explicit
// ALCompiler.ToRecordRef wrapper. Adding an implicit conversion operator on
// MockRecordRef ensures all such code paths are handled.

codeunit 1275002 "RecRef Implicit Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // Helper: same-codeunit procedure taking RecordRef by value
    procedure GetTableNoFromRef(RecRef: RecordRef): Integer
    begin
        exit(RecRef.Number);
    end;

    // ---------------------------------------------------------------
    // Positive: pass Record to cross-codeunit RecordRef by-value param
    // ---------------------------------------------------------------

    [Test]
    procedure CrossCU_RecordToRecordRef_FieldCount()
    var
        Rec: Record "Implicit Conv Table";
        Helper: Codeunit "RecRef Implicit Helper";
    begin
        // [GIVEN] A Record variable for table 1275001 (2 fields)
        // [WHEN]  Passed to a RecordRef parameter in a different codeunit
        // [THEN]  Returns 2
        Assert.AreEqual(2, Helper.GetFieldCountFromRef(Rec),
            'Cross-CU: FieldCount from Record passed as RecordRef must be 2');
    end;

    [Test]
    procedure CrossCU_RecordToRecordRef_TableNo()
    var
        Rec: Record "Implicit Conv Table";
        Helper: Codeunit "RecRef Implicit Helper";
    begin
        // [GIVEN] A Record variable bound to table 1275001
        // [WHEN]  Passed to a RecordRef parameter in a different codeunit
        // [THEN]  Number returns the table ID
        Assert.AreEqual(1275001, Helper.GetTableNoFromRef(Rec),
            'Cross-CU: Table number from Record passed as RecordRef must be 1275001');
    end;

    // ---------------------------------------------------------------
    // Same-codeunit: Record passed to local RecordRef by-value param
    // This exercises the ALCompiler.ToRecordRef path already fixed.
    // ---------------------------------------------------------------

    [Test]
    procedure SameCU_RecordToRecordRef_TableNo()
    var
        Rec: Record "Implicit Conv Table";
    begin
        // [GIVEN] A Record variable bound to table 1275001
        // [WHEN]  Passed to a RecordRef parameter in the same codeunit
        // [THEN]  Number returns the table ID
        Assert.AreEqual(1275001, GetTableNoFromRef(Rec),
            'Same-CU: Table number from Record passed as RecordRef must be 1275001');
    end;

    // ---------------------------------------------------------------
    // Negative: unbound RecordRef has Number == 0
    // ---------------------------------------------------------------

    [Test]
    procedure CrossCU_UnboundRecordRef_TableNoIsZero()
    var
        RecRef: RecordRef;
        Helper: Codeunit "RecRef Implicit Helper";
    begin
        // [GIVEN] An unbound RecordRef
        // [WHEN]  Passed to the helper
        // [THEN]  Number is 0
        Assert.AreEqual(0, Helper.GetTableNoFromRef(RecRef),
            'Unbound RecordRef must have Number 0');
    end;
}
