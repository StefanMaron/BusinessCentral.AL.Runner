codeunit 61401 "CCP CopyCompany Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CopyCompany_NoOp_TypicalCompanyNames()
    var
        Helper: Codeunit "CCP Helper";
    begin
        // Positive: Database.CopyCompany must execute without error (no-op stub).
        Helper.CallCopyCompany('Source Company', 'Destination Company');
        Assert.IsTrue(true, 'CopyCompany must complete without error');
    end;

    [Test]
    procedure CopyCompany_NoOp_EmptyNames()
    var
        Helper: Codeunit "CCP Helper";
    begin
        // Positive: CopyCompany with empty strings must also be a no-op.
        Helper.CallCopyCompany('', '');
        Assert.IsTrue(true, 'CopyCompany with empty names must complete without error');
    end;

    [Test]
    procedure CopyCompany_CalledTwice_NoError()
    var
        Helper: Codeunit "CCP Helper";
    begin
        // Edge case: calling CopyCompany multiple times must not error.
        Helper.CallCopyCompany('A', 'B');
        Helper.CallCopyCompany('C', 'D');
        Assert.IsTrue(true, 'CopyCompany called twice must complete without error');
    end;

    [Test]
    procedure AddWithBonus_ProvingCompilationUnitLive()
    var
        Helper: Codeunit "CCP Helper";
    begin
        // Proving: the codeunit is live — real computation returns a+b+1.
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
    end;

    [Test]
    procedure AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "CCP Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;
}
