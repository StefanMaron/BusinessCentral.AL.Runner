codeunit 61601 "EDT ExportData Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ExportData_NoOp_WithFileName()
    var
        Helper: Codeunit "EDT Helper";
    begin
        // Positive: Database.ExportData('test.dat') must execute without error (no-op stub).
        Helper.CallExportData('test.dat');
        Assert.IsTrue(true, 'ExportData(''test.dat'') must complete without error');
    end;

    [Test]
    procedure ExportData_NoOp_EmptyFileName()
    var
        Helper: Codeunit "EDT Helper";
    begin
        // Positive: Database.ExportData('') must also be a no-op.
        Helper.CallExportData('');
        Assert.IsTrue(true, 'ExportData('''') must complete without error');
    end;

    [Test]
    procedure ExportData_CalledTwice_NoError()
    var
        Helper: Codeunit "EDT Helper";
    begin
        // Edge case: calling ExportData multiple times must not error.
        Helper.CallExportData('first.dat');
        Helper.CallExportData('second.dat');
        Assert.IsTrue(true, 'ExportData called twice must complete without error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "EDT Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "EDT Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
