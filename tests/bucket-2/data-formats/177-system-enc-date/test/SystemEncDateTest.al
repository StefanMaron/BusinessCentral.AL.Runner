codeunit 104001 "SED Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SED Src";

    [Test]
    procedure EncryptionEnabled_FalseInRunner()
    begin
        Assert.IsFalse(Src.IsEncEnabled(), 'EncryptionEnabled must be false in standalone runner');
    end;

    [Test]
    procedure EncryptionKeyExists_FalseInRunner()
    begin
        Assert.IsFalse(Src.EncKeyExists(), 'EncryptionKeyExists must be false in standalone runner');
    end;

    [Test]
    procedure DMY2Date_ProducesCorrectDate()
    begin
        Assert.AreEqual(20260417D, Src.DMYToDate(17, 4, 2026), 'DMY2Date(17,4,2026) must return 2026-04-17');
    end;

    [Test]
    procedure DMY2Date_MinDate()
    begin
        Assert.AreEqual(17530101D, Src.DMYToDate(1, 1, 1753), 'DMY2Date(1,1,1753) must return 1753-01-01');
    end;

    [Test]
    procedure DWY2Date_Week1Day1_Is1753Jan01()
    begin
        // Jan 1, 1753 is a Monday (Day=1). Week 1 of 1753 starts on Jan 1.
        // So DWY2Date(Day=1, Week=1, Year=1753) = 1753-01-01.
        Assert.AreEqual(17530101D, Src.DWYToDate(1, 1, 1753), 'DWY2Date(1,1,1753) must return 1753-01-01');
    end;

    [Test]
    procedure NormalDate_OfNormalDate_ReturnsSame()
    begin
        Assert.AreEqual(20260417D, Src.NDate(20260417D), 'NormalDate of a normal date must return itself');
    end;

    [Test]
    procedure ClosingDate_ThenNormalDate_RoundTrips()
    var
        Closing: Date;
    begin
        Closing := Src.CDate(20261231D);
        Assert.AreEqual(20261231D, Src.NDate(Closing), 'NormalDate(ClosingDate(D)) must return D');
    end;

    [Test]
    procedure Variant2Date_ExtractsDate()
    var
        V: Variant;
    begin
        V := 20260417D;
        Assert.AreEqual(20260417D, Src.VarToDate(V), 'Variant2Date must extract the date value');
    end;

    [Test]
    procedure Variant2Time_ExtractsTime()
    var
        V: Variant;
    begin
        V := 120000T;
        Assert.AreEqual(120000T, Src.VarToTime(V), 'Variant2Time must extract the time value');
    end;

    [Test]
    procedure DaTi2Variant_IsVariant()
    var
        V: Variant;
    begin
        V := Src.DaTiToVar(20260417D, 120000T);
        Assert.IsTrue(V.IsDateTime(), 'DaTi2Variant result must be a DateTime variant');
    end;
}
