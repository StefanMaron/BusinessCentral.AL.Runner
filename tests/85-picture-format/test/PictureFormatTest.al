codeunit 85101 PictureFormatTest
{
    Subtype = Test;
    var Assert: Codeunit Assert;
    var Helper: Codeunit PictureFormatHelper;

    [Test]
    procedure TestDecimalPrecision_1_2()
    begin
        Assert.AreEqual('1.57', Helper.FormatDecimalPrecision(1.567, 1, 2), 'Precision 1:2 should round to 2 decimals');
    end;

    [Test]
    procedure TestDecimalPrecision_0_0()
    begin
        Assert.AreEqual('2', Helper.FormatDecimalPrecision(1.567, 0, 0), 'Precision 0:0 should round to integer');
    end;

    [Test]
    procedure TestDecimalPrecision_2_4()
    begin
        // 1.567 has 3 decimal places; Precision 2:4 means show 2..4, so 3 is within range -> '1.567'
        Assert.AreEqual('1.567', Helper.FormatDecimalPrecision(1.567, 2, 4), 'Precision 2:4 should show at least 2 and up to 4 decimals');
    end;

    [Test]
    procedure TestDecimalPrecision_0_5()
    begin
        Assert.AreEqual('1.567', Helper.FormatDecimalPrecision(1.567, 0, 5), 'Precision 0:5 should trim trailing zeros up to 5 places');
    end;

    [Test]
    procedure TestStandardFormat_0()
    begin
        Assert.AreEqual('1.567', Helper.FormatStandardFormat(1.567, 0), 'Standard Format 0 should use default decimal formatting');
    end;

    [Test]
    procedure TestStandardFormat_1()
    begin
        Assert.AreEqual('2', Helper.FormatStandardFormat(1.567, 1), 'Standard Format 1 should round to integer');
    end;

    [Test]
    procedure TestTimePicture()
    var
        T: Time;
    begin
        T := 093000T; // 09:30:00
        Assert.AreEqual('09:30', Helper.FormatTimePicture(T), 'Time picture Hours24:Minutes should format correctly');
    end;

    [Test]
    procedure TestTimePicture_Midnight()
    var
        T: Time;
    begin
        T := 000000T; // 00:00:00
        Assert.AreEqual('00:00', Helper.FormatTimePicture(T), 'Midnight should format as 00:00');
    end;

    [Test]
    procedure TestTimePicture_Noon()
    var
        T: Time;
    begin
        T := 120000T; // 12:00:00
        Assert.AreEqual('12:00', Helper.FormatTimePicture(T), 'Noon should format as 12:00');
    end;
}
