codeunit 61301 "DFI DataFileInformation Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure DataFileInformation_NoOp_ShowDialogTrue()
    var
        Helper: Codeunit "DFI Helper";
    begin
        // Positive: Database.DataFileInformation(true) must execute without error (no-op stub).
        Helper.CallDataFileInformation(true);
        Assert.IsTrue(true, 'DataFileInformation(true) must complete without error');
    end;

    [Test]
    procedure DataFileInformation_NoOp_ShowDialogFalse()
    var
        Helper: Codeunit "DFI Helper";
    begin
        // Positive: Database.DataFileInformation(false) must also be a no-op.
        Helper.CallDataFileInformation(false);
        Assert.IsTrue(true, 'DataFileInformation(false) must complete without error');
    end;

    [Test]
    procedure DataFileInformation_CalledTwice_NoError()
    var
        Helper: Codeunit "DFI Helper";
    begin
        // Edge case: calling DataFileInformation multiple times must not error.
        Helper.CallDataFileInformation(false);
        Helper.CallDataFileInformation(true);
        Assert.IsTrue(true, 'DataFileInformation called twice must complete without error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "DFI Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "DFI Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
