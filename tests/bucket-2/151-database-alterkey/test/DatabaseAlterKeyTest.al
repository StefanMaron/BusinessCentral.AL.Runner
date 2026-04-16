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
        // Positive: Database.AlterKey(keyRef, true) must execute without error (no-op stub).
        Helper.CallAlterKeyOnPK(true);
        Assert.IsTrue(true, 'AlterKey(keyRef, true) must complete without error');
    end;

    [Test]
    procedure AlterKey_NoOp_ClusteredFalse()
    var
        Helper: Codeunit "DAK Helper";
    begin
        // Positive: Database.AlterKey(keyRef, false) must also be a no-op.
        Helper.CallAlterKeyOnPK(false);
        Assert.IsTrue(true, 'AlterKey(keyRef, false) must complete without error');
    end;

    [Test]
    procedure AlterKey_CalledTwice_NoError()
    var
        Helper: Codeunit "DAK Helper";
    begin
        // Edge case: calling AlterKey multiple times must not error.
        Helper.CallAlterKeyOnPK(true);
        Helper.CallAlterKeyOnPK(false);
        Assert.IsTrue(true, 'AlterKey called twice must complete without error');
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
