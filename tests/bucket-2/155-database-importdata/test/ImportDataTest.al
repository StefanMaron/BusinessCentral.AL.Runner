codeunit 61501 "IDT ImportData Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ImportData_NoOp_ShowDialogTrue()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Positive: Database.ImportData(true, ...) must execute without error (no-op stub).
        Helper.CallImportData(true);
        Assert.IsTrue(true, 'ImportData(true) must complete without error');
    end;

    [Test]
    procedure ImportData_NoOp_ShowDialogFalse()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Positive: Database.ImportData(false, ...) must also be a no-op.
        Helper.CallImportData(false);
        Assert.IsTrue(true, 'ImportData(false) must complete without error');
    end;

    [Test]
    procedure ImportData_CalledTwice_NoError()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Edge case: calling ImportData multiple times must not error.
        Helper.CallImportData(false);
        Helper.CallImportData(true);
        Assert.IsTrue(true, 'ImportData called twice must complete without error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
