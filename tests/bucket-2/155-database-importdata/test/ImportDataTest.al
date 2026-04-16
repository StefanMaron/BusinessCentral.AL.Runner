codeunit 61501 "IDT ImportData Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ImportData_NoOp_TableNumber18()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Positive: Database.ImportData(18, ...) must execute without error (no-op stub).
        Helper.CallImportData(18);
        Assert.IsTrue(true, 'ImportData(18) must complete without error');
    end;

    [Test]
    procedure ImportData_NoOp_TableNumber0()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Positive: Database.ImportData(0, ...) must also be a no-op.
        Helper.CallImportData(0);
        Assert.IsTrue(true, 'ImportData(0) must complete without error');
    end;

    [Test]
    procedure ImportData_CalledTwice_NoError()
    var
        Helper: Codeunit "IDT Helper";
    begin
        // Edge case: calling ImportData multiple times must not error.
        Helper.CallImportData(18);
        Helper.CallImportData(27);
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
