codeunit 59391 "TT Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "TT Src";

    [Test]
    procedure DateTimeToText_ReturnsNonEmpty()
    var
        dt: DateTime;
    begin
        dt := CreateDateTime(DMY2Date(1, 1, 2025), 000000T);
        Assert.AreNotEqual('', Src.FormatDateTime(dt),
            'DateTime.ToText must return a non-empty string');
    end;

    [Test]
    procedure DateTimeToText_ContainsYear()
    begin
        // Format is locale-dependent, but the 4-digit year 2025 must appear
        // in any sensible DateTime string representation.
        Assert.IsTrue(
            StrPos(Src.FormatKnownDateTime(), '2025') > 0,
            'DateTime.ToText for 2025-06-15 12:00 must contain "2025"');
    end;

    [Test]
    procedure DateTimeToText_DifferentValues_DifferentStrings()
    var
        a: DateTime;
        b: DateTime;
    begin
        // Negative: guard against a stub that returns the same string for
        // all inputs — two different DateTimes must produce distinct text.
        a := CreateDateTime(DMY2Date(1, 1, 2020), 120000T);
        b := CreateDateTime(DMY2Date(1, 1, 2025), 120000T);
        Assert.AreNotEqual(Src.FormatDateTime(a), Src.FormatDateTime(b),
            'Different DateTime values must produce different text');
    end;

    [Test]
    procedure DecimalToText_ReturnsNonEmpty()
    var
        d: Decimal;
    begin
        d := 42.5;
        Assert.AreNotEqual('', Src.FormatDecimal(d),
            'Decimal.ToText must return a non-empty string');
    end;

    [Test]
    procedure DecimalToText_Zero_ContainsZero()
    begin
        // Decimal 0 must produce a string containing '0'.
        Assert.IsTrue(
            StrPos(Src.FormatDecimalZero(), '0') > 0,
            'Decimal 0.ToText must contain the digit 0');
    end;

    [Test]
    procedure DecimalToText_SpecificValue_ContainsDigits()
    var
        s: Text;
    begin
        // Decimal 123.45 must contain at least the integer-part digits.
        s := Src.FormatDecimalSpecific();
        Assert.IsTrue(StrPos(s, '123') > 0, 'Decimal 123.45.ToText must contain "123"');
        Assert.IsTrue(StrPos(s, '45') > 0, 'Decimal 123.45.ToText must contain "45"');
    end;

    [Test]
    procedure DecimalToText_DifferentValues_DifferentStrings()
    var
        a: Decimal;
        b: Decimal;
    begin
        // Negative: guard against a stub that returns the same string for all inputs.
        a := 1;
        b := 9999;
        Assert.AreNotEqual(Src.FormatDecimal(a), Src.FormatDecimal(b),
            'Different Decimal values must produce different text');
    end;

    [Test]
    procedure DateTimeToText_NotRawZero_NegativeTrap()
    var
        dt: DateTime;
    begin
        // Negative: guard against returning just "0" (would suggest a default integer).
        dt := CreateDateTime(DMY2Date(15, 6, 2025), 120000T);
        Assert.AreNotEqual('0', Src.FormatDateTime(dt),
            'DateTime.ToText must not return just "0"');
    end;
}
