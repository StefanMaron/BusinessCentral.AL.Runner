codeunit 61221 "CLF CheckLicenseFile Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CheckLicenseFile_NoOp_KeyNumber1()
    var
        Helper: Codeunit "CLF Helper";
    begin
        // Positive: Database.CheckLicenseFile(1) must execute without error (no-op stub).
        Helper.CallCheckLicenseFile(1);
        Assert.IsTrue(true, 'CheckLicenseFile(1) must complete without error');
    end;

    [Test]
    procedure CheckLicenseFile_NoOp_KeyNumber0()
    var
        Helper: Codeunit "CLF Helper";
    begin
        // Positive: Database.CheckLicenseFile(0) must also be a no-op.
        Helper.CallCheckLicenseFile(0);
        Assert.IsTrue(true, 'CheckLicenseFile(0) must complete without error');
    end;

    [Test]
    procedure CheckLicenseFile_CalledTwice_NoError()
    var
        Helper: Codeunit "CLF Helper";
    begin
        // Edge case: calling CheckLicenseFile multiple times must not error.
        Helper.CallCheckLicenseFile(1);
        Helper.CallCheckLicenseFile(2);
        Assert.IsTrue(true, 'CheckLicenseFile() called twice must complete without error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "CLF Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "CLF Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
