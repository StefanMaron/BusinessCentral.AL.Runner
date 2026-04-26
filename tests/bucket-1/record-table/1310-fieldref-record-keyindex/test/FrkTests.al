/// Tests for FieldRef.Record().KeyIndex(n) — exercises MockFieldRef.ALKeyIndex.
///
/// The BC transpiler lowers  KeyRef := FldRef.Record().KeyIndex(1)
/// to  KeyRef.ALAssign(FldRef.ALKeyIndex(compilationTarget, 1)).
/// MockFieldRef must therefore expose ALKeyIndex delegating to its owning
/// MockRecordRef.
codeunit 1310003 "FRK Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "FRK Src";

    // ── Positive: KeyRef.FieldCount via the FieldRef chain ──────────────

    [Test]
    procedure FieldRef_Record_KeyIndex_FieldCount()
    begin
        // [GIVEN] FRK Test Entry has a 2-field PK (Id, Code)
        // [WHEN]  KRef := FldRef.Record().KeyIndex(1)
        // [THEN]  KRef.FieldCount = 2
        Assert.AreEqual(2, Src.GetKeyFieldCountViaFieldRef(),
            'KeyRef.FieldCount via FldRef.Record().KeyIndex(1) must equal 2');
    end;

    // ── Positive: KeyRef.FieldIndex field numbers ────────────────────────

    [Test]
    procedure FieldRef_Record_KeyIndex_FirstPkField()
    begin
        // [GIVEN] PK = (Id field 1, Code field 2)
        // [WHEN]  KRef.FieldIndex(1)
        // [THEN]  field number = 1
        Assert.AreEqual(1, Src.GetPkFieldNoViaFieldRef(1),
            'First PK field accessed via FldRef chain must be field 1');
    end;

    [Test]
    procedure FieldRef_Record_KeyIndex_SecondPkField()
    begin
        // [GIVEN] PK = (Id field 1, Code field 2)
        // [WHEN]  KRef.FieldIndex(2)
        // [THEN]  field number = 2
        Assert.AreEqual(2, Src.GetPkFieldNoViaFieldRef(2),
            'Second PK field accessed via FldRef chain must be field 2');
    end;

    // ── Negative: out-of-range key index ────────────────────────────────

    [Test]
    procedure FieldRef_Record_KeyIndex_OutOfRange_Throws()
    begin
        // [WHEN]  FldRef.Record().KeyIndex(99)
        // [THEN]  error contains "out of range"
        asserterror Src.GetKeyIndexOutOfRange();
        Assert.ExpectedError('out of range');
    end;
}
