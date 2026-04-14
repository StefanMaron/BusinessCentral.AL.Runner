codeunit 81000 "Test IsInWriteTransaction"
{
    Subtype = Test;

    var Assert: Codeunit Assert;

    [Test]
    procedure IsInWriteTransactionReturnsFalse()
    var
        InTrans: Boolean;
    begin
        // Positive: IsInWriteTransaction should return false in runner context
        InTrans := Database.IsInWriteTransaction();
        Assert.AreEqual(false, InTrans, 'Expected IsInWriteTransaction to return false');
    end;

    [Test]
    procedure IsInWriteTransactionIsNotTrue()
    begin
        // Negative: prove it does NOT return true (a mock returning true would fail this)
        Assert.AreNotEqual(true, Database.IsInWriteTransaction(), 'IsInWriteTransaction must not return true');
    end;
}
