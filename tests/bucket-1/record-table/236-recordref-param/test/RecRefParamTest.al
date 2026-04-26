// Issue 1242: CS1503 when Record variable is passed to a RecordRef by-value
// parameter inside the same codeunit.
// BC emits ALCompiler.ToRecordRef(scope, rec.Target) for this pattern; the
// rewriter must convert it to MockRecordRef.FromHandle(handle) so MockRecordHandle
// is not passed where NavRecord was expected.

codeunit 237001 "RecRef Param Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ---------------------------------------------------------------
    // Helper procedures: take RecordRef by value (same codeunit).
    // ---------------------------------------------------------------

    procedure FieldCountFromRecordRef(RecRef: RecordRef): Integer
    begin
        exit(RecRef.FieldCount);
    end;

    procedure IsOpenFromRecordRef(RecRef: RecordRef): Boolean
    begin
        exit(RecRef.Number <> 0);
    end;

    // ---------------------------------------------------------------
    // Positive: passing a Record variable to a RecordRef by-value param
    // in the same codeunit — previously caused CS1503.
    // ---------------------------------------------------------------

    [Test]
    procedure RecordPassedToRecordRefParam_FieldCount()
    var
        Rec: Record "RecRef Param Table";
    begin
        // [GIVEN] A Record variable for table 237000 (2 fields)
        // [WHEN]  Passed by value to FieldCountFromRecordRef in this codeunit
        // [THEN]  Returns 2 — proves the mock RecordRef wraps the Record correctly
        Assert.AreEqual(2, FieldCountFromRecordRef(Rec), 'FieldCount from Record→RecordRef must be 2');
    end;

    [Test]
    procedure RecordPassedToRecordRefParam_IsOpen_ReturnsTrue()
    var
        Rec: Record "RecRef Param Table";
    begin
        // [GIVEN] A Record variable (always has a table number bound)
        // [WHEN]  Passed by value to IsOpenFromRecordRef
        // [THEN]  Number != 0 → true
        Assert.IsTrue(IsOpenFromRecordRef(Rec), 'Record converted to RecordRef must report as open');
    end;

    // ---------------------------------------------------------------
    // Negative: unbound RecordRef (default) has Number == 0.
    // ---------------------------------------------------------------

    [Test]
    procedure UnboundRecordRefParam_IsOpen_ReturnsFalse()
    var
        RecRef: RecordRef;
    begin
        // [GIVEN] A plain unbound RecordRef variable
        // [WHEN]  Passed by value to IsOpenFromRecordRef
        // [THEN]  Number == 0 → false
        Assert.IsFalse(IsOpenFromRecordRef(RecRef), 'Unbound RecordRef must not report as open');
    end;

    [Test]
    procedure RecordPassedToRecordRefParam_FieldCountNotZero()
    var
        Rec: Record "RecRef Param Table";
    begin
        // [GIVEN] A Record variable (2 fields)
        // [WHEN]  FieldCountFromRecordRef is called
        // [THEN]  Result is not 0 — a no-op mock would return 0 and fail this
        Assert.AreNotEqual(0, FieldCountFromRecordRef(Rec), 'FieldCount must not be 0 for a bound Record');
    end;
}
