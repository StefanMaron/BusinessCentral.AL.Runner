/// Tests for integer-to-text conversion behaviour (issue #1426).
///
/// BC 26+ allows implicit Integer→Text coercion.  When BC 26+ AL is transpiled to C#
/// the runner's CS1503Fixer wraps each int-to-string argument with AlCompat.Format().
/// These tests prove that AlCompat.Format() (exercised here via AL's built-in Format())
/// produces the correct string for positive integers, negative integers, and zero.
///
/// A failing mock (one that always returns '' or '0') would not pass these tests.
codeunit 313001 "Int To Text Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "Int To Text Src";

    // ── Format(Integer) round-trip — positive ──────────────────────────────

    [Test]
    procedure IntToText_PositiveSmall_ReturnsCorrectString()
    begin
        // [GIVEN] integer 42
        // [WHEN]  Format(42) is called
        // [THEN]  result is '42'
        Assert.AreEqual('42', Src.IntToText(42), 'Format(42) must return ''42''');
    end;

    [Test]
    procedure IntToText_PositiveLarge_ReturnsCorrectString()
    begin
        // [GIVEN] integer 1000000
        // [WHEN]  Format(1000000) is called
        // [THEN]  result is '1000000'
        Assert.AreEqual('1000000', Src.IntToText(1000000), 'Format(1000000) must return ''1000000''');
    end;

    [Test]
    procedure IntToText_Zero_ReturnsZeroString()
    begin
        // [GIVEN] integer 0
        // [WHEN]  Format(0) is called
        // [THEN]  result is '0'
        Assert.AreEqual('0', Src.IntToText(0), 'Format(0) must return ''0''');
    end;

    // ── Format(Integer) round-trip — negative ─────────────────────────────

    [Test]
    procedure IntToText_NegativeSmall_ReturnsNegativeString()
    begin
        // [GIVEN] integer -7
        // [WHEN]  Format(-7) is called
        // [THEN]  result is '-7'
        Assert.AreEqual('-7', Src.IntToText(-7), 'Format(-7) must return ''-7''');
    end;

    [Test]
    procedure IntToText_NegativeLarge_ReturnsNegativeString()
    begin
        // [GIVEN] integer -999999
        // [WHEN]  Format(-999999) is called
        // [THEN]  result is '-999999'
        Assert.AreEqual('-999999', Src.IntToText(-999999), 'Format(-999999) must return ''-999999''');
    end;

    // ── FormatThenAccept — the CS1503 pattern ─────────────────────────────
    // Exercises the exact pattern CS1503Fixer synthesises:
    //   AcceptText(AlCompat.Format(intArg))
    // The AL source calls AcceptText(Format(Value)) explicitly since BC 17
    // rejects implicit Integer→Text without Format().

    [Test]
    procedure FormatThenAccept_PositiveInt_PassesThroughCorrectly()
    begin
        // [GIVEN] integer 123 formatted and passed to a Text parameter
        // [WHEN]  FormatThenAccept(123) is called
        // [THEN]  result is '123'
        Assert.AreEqual('123', Src.FormatThenAccept(123),
            'FormatThenAccept(123) must return ''123''');
    end;

    [Test]
    procedure FormatThenAccept_NegativeInt_PassesThroughCorrectly()
    begin
        // [GIVEN] integer -456 formatted and passed to a Text parameter
        // [WHEN]  FormatThenAccept(-456) is called
        // [THEN]  result is '-456'
        Assert.AreEqual('-456', Src.FormatThenAccept(-456),
            'FormatThenAccept(-456) must return ''-456''');
    end;

    [Test]
    procedure FormatThenAccept_Zero_PassesThroughCorrectly()
    begin
        // [GIVEN] integer 0 formatted and passed to a Text parameter
        // [WHEN]  FormatThenAccept(0) is called
        // [THEN]  result is '0'
        Assert.AreEqual('0', Src.FormatThenAccept(0),
            'FormatThenAccept(0) must return ''0''');
    end;
}
