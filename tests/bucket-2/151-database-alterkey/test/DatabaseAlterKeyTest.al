codeunit 61001 "DAK AlterKey Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure AlterKey_NoOp_ClusteredTrue()
    var
        Helper: Codeunit "DAK Helper";
    begin
        // Positive: Database.AlterKey(true, ...) must execute without error (no-op stub).
        Helper.CallAlterKey(true, 'PK', 'Item');
        Assert.IsTrue(true, 'AlterKey(true) must complete without error');
    end;

    [Test]
    procedure AlterKey_NoOp_ClusteredFalse()
    var
        Helper: Codeunit "DAK Helper";
    begin
        // Positive: Database.AlterKey(false, ...) must also be a no-op.
        Helper.CallAlterKey(false, 'SecondaryKey', 'Customer');
        Assert.IsTrue(true, 'AlterKey(false) must complete without error');
    end;

    [Test]
    procedure AlterKey_NoOp_EmptyKeyName()
    var
        Helper: Codeunit "DAK Helper";
    begin
        // Edge case: empty key name must still execute without error.
        Helper.CallAlterKey(false, '', 'Vendor');
        Assert.IsTrue(true, 'AlterKey with empty key name must complete without error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "DAK Helper";
    begin
        // Proving: the codeunit is live, not a stub — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "DAK Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
