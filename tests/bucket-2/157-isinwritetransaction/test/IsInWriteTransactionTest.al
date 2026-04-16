codeunit 61701 "IWT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: IsInWriteTransaction() always returns false standalone.
    // The runner has no real DB transactions; false is the correct stub.
    // ------------------------------------------------------------------

    [Test]
    procedure IsInWriteTransaction_ReturnsFalse()
    var
        H: Codeunit "IWT Helper";
    begin
        Assert.IsFalse(H.GetIsInWriteTransaction(), 'IsInWriteTransaction must return false in standalone runner');
    end;

    [Test]
    procedure IsInWriteTransaction_ReturnsFalse_ExactValue()
    var
        H: Codeunit "IWT Helper";
    begin
        Assert.AreEqual(false, H.GetIsInWriteTransaction(), 'IsInWriteTransaction must return exactly false');
    end;

    [Test]
    procedure IsInWriteTransaction_AfterInsert_StillFalse()
    var
        H: Codeunit "IWT Helper";
        Rec: Record "IWT Dummy";
    begin
        Assert.IsFalse(H.InsertAndCheck(Rec), 'IsInWriteTransaction must return false even after record insert');
    end;

    // ------------------------------------------------------------------
    // Negative: the return value is not true (runner has no write txn).
    // ------------------------------------------------------------------

    [Test]
    procedure IsInWriteTransaction_IsNotTrue()
    var
        H: Codeunit "IWT Helper";
    begin
        Assert.AreNotEqual(true, H.GetIsInWriteTransaction(), 'IsInWriteTransaction must not return true in standalone runner');
    end;
}
