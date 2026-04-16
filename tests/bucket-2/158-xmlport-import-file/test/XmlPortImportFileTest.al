codeunit 61802 "XIF XmlPortImportFile Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ImportFile_NoArgs_NoError()
    var
        Helper: Codeunit "XIF Helper";
    begin
        // Positive: xp.ImportFile() must be a no-op stub — no error.
        Helper.CallImportFile();
        Assert.IsTrue(true, 'XmlPort.ImportFile() must complete without error');
    end;

    [Test]
    procedure ImportFile_CalledTwice_NoError()
    var
        Helper: Codeunit "XIF Helper";
    begin
        // Edge case: calling ImportFile multiple times must not error.
        Helper.CallImportFile();
        Helper.CallImportFile();
        Assert.IsTrue(true, 'XmlPort.ImportFile() called twice must complete without error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "XIF Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "XIF Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
