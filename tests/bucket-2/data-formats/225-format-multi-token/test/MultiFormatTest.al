codeunit 50226 "Multi Format Tests"
{
    Subtype = Test;

    var
        Helper: Codeunit "Multi Format Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure MultiToken_PrecisionStandard_Integer()
    begin
        // <Precision,2:2><Standard Format,0> should force 2 decimals (Precision wins for decimals)
        Assert.AreEqual('1234.00', Helper.FormatPrecisionStd(1234), 'Integer value 1234 with <Precision,2:2><Standard Format,0> must show 2 decimals');
    end;

    [Test]
    procedure MultiToken_PrecisionStandard_Fractional()
    begin
        Assert.AreEqual('1.50', Helper.FormatPrecisionStd(1.5), 'Value 1.5 with <Precision,2:2><Standard Format,0> must show 1.50');
    end;

    [Test]
    procedure MultiToken_PrecisionStandard_TruncatesExtraDecimals()
    begin
        // 1.567 rounded to 2 decimals = 1.57
        Assert.AreEqual('1.57', Helper.FormatPrecisionStd(1.567), 'Value 1.567 with <Precision,2:2><Standard Format,0> must round to 1.57');
    end;

    [Test]
    procedure MixedDateAndDecimal_InSeparateCalls()
    var
        ResultText: Text;
    begin
        ResultText := Helper.FormatDatePrecision(20260101D, 1234.56);
        Assert.AreEqual('2026-01-01 1234.56', ResultText, 'Date and decimal formatting must compose correctly');
    end;

    [Test]
    procedure SingleToken_Precision_StillWorks()
    begin
        // Regression: single-token precision still works after multi-token support added
        Assert.AreEqual('1234.00', Helper.FormatPrecisionOnly(1234), 'Single-token <Precision,2:2> must still produce 2 decimals');
    end;

    [Test]
    procedure UnknownToken_PreservedLiterally()
    begin
        // Negative: when the format string has no recognised tokens, runner falls back to default formatting
        // (matches current behavior for unsupported picture strings — value formatted without picture)
        Assert.AreEqual('5', Helper.FormatUnknownToken(5), 'Unknown token falls back to default numeric formatting');
    end;
}
