// TestPageFieldTextTests — contract tests for the runner's own TestPage field-text helpers
// (issues #5411 and #5369): TestPageNumericValue.FormatObject / TryResolveBlank,
// TestPageBlanking, TestPageTemporalText, TestPageGuidDurationText and the Duration arm of
// TestPageTemporalValue.TryResolve.
//
// These are claims about OUR helpers, not about Business Central. What a service tier shows
// and accepts for every field type is asserted upstream in
// StefanMaron/BusinessCentral.AL.Language.Tests codeunit 69932 "TPF Tests". What is pinned here
// is each helper's decline direction: every one sits in a `??` chain in the three field classes,
// so a helper that claimed a value it should decline would steal the read from the next arm.
using System;
using System.Globalization;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageFieldTextTests
{
    private static NavDecimal Dec(decimal v) => (NavDecimal)NavDecimal.Create((Decimal18)v);

    // ── Decimal: the expected side is spelled the way the getter spells the control ──────

    [Theory]
    [InlineData("#,##0.00", 1234567.89, "1,234,567.89")]
    [InlineData("#,##0.00", 999.5, "999.50")]
    [InlineData("#,##0.000", 1000, "1,000.000")]
    [InlineData("#,##0.##", 1000, "1,000")]
    public void FormatObject_Decimal_UsesTheControlFormat(string format, double value, string expected)
        => Assert.Equal(expected, TestPageNumericValue.FormatObject((decimal)value, format));

    [Fact]
    public void FormatObject_Decimal_AgreesWithTheGetterForTheSameValueAndFormat()
        => Assert.Equal(TestPageNumericValue.Format(Dec(4175412.34m), "#,##0.00"),
                        TestPageNumericValue.FormatObject(4175412.34m, "#,##0.00"));

    [Theory]
    [InlineData(1000)]
    [InlineData(1234567L)]
    [InlineData(true)]
    [InlineData("1000")]
    public void FormatObject_NotADecimal_DeclinesSoTheNextArmReadsIt(object value)
        => Assert.Null(TestPageNumericValue.FormatObject(value, "#,##0.00"));

    [Fact]
    public void FormatObject_Null_Declines() => Assert.Null(TestPageNumericValue.FormatObject(null, "#,##0.00"));

    [Theory]
    [InlineData(NavType.Decimal)]
    [InlineData(NavType.Integer)]
    [InlineData(NavType.BigInteger)]
    public void TryResolveBlank_BlankTextOnANumericControl_IsZero(NavType type)
    {
        Assert.True(TestPageNumericValue.TryResolveBlank(type, "", out var zero));
        Assert.Equal("0", zero!.ToString());
        Assert.True(TestPageNumericValue.TryResolveBlank(type, "  ", out _));
    }

    [Theory]
    [InlineData(NavType.Decimal, "0")]
    [InlineData(NavType.Decimal, "1,000.00")]
    [InlineData(NavType.Text, "")]
    [InlineData(NavType.Code, "")]
    [InlineData(NavType.Date, "")]
    [InlineData(NavType.Boolean, "")]
    public void TryResolveBlank_DeclinesNonBlankTextAndNonNumericControls(NavType type, string text)
    {
        Assert.False(TestPageNumericValue.TryResolveBlank(type, text, out var resolved));
        Assert.Null(resolved);
    }

    // ── BlankZero / BlankNumbers: BC's NavBlankNumbersDecorator.BlankFormatApplies ────────

    // Each row is one cell of BC's own table: which of {negative, zero, positive} a specifier blanks.
    [Theory]
    [InlineData(BlankNumbers.DontBlank, false, false, false, false)]
    [InlineData(BlankNumbers.BlankNeg, false, true, false, false)]
    [InlineData(BlankNumbers.BlankNegAndZero, false, true, true, false)]
    [InlineData(BlankNumbers.BlankZero, false, false, true, false)]
    [InlineData(BlankNumbers.BlankZeroAndPos, false, false, true, true)]
    [InlineData(BlankNumbers.BlankPos, false, false, false, true)]
    [InlineData(BlankNumbers.DontBlank, true, false, true, false)]   // the BlankZero property alone
    [InlineData(BlankNumbers.BlankNeg, true, true, true, false)]     // ... adds to a BlankNumbers
    public void Applies_BlanksExactlyTheSignsTheSpecifierNames(
        BlankNumbers numbers, bool zeroFlag, bool negative, bool zero, bool positive)
    {
        var spec = new TestPageBlanking.Spec(numbers, zeroFlag);
        Assert.Equal(negative, TestPageBlanking.Applies(spec, -5m));
        Assert.Equal(zero, TestPageBlanking.Applies(spec, 0m));
        Assert.Equal(positive, TestPageBlanking.Applies(spec, 5m));
        // The three integer widths follow the same table.
        Assert.Equal(negative, TestPageBlanking.Applies(spec, -5));
        Assert.Equal(zero, TestPageBlanking.Applies(spec, 0));
        Assert.Equal(positive, TestPageBlanking.Applies(spec, 5L));
    }

    [Fact]
    public void Applies_NoSpecOrNoValue_NeverBlanks()
    {
        Assert.False(TestPageBlanking.Applies(null, 0m));
        Assert.False(TestPageBlanking.Applies(new TestPageBlanking.Spec(BlankNumbers.DontBlank, false), 0m));
        Assert.False(TestPageBlanking.Applies(new TestPageBlanking.Spec(BlankNumbers.BlankZero, false), null));
    }

    [Fact]
    public void Applies_BlankZeroOnBooleanDurationAndTemporal_BlanksOnlyTheirZero()
    {
        var spec = new TestPageBlanking.Spec(BlankNumbers.DontBlank, true);
        Assert.True(TestPageBlanking.Applies(spec, false));
        Assert.False(TestPageBlanking.Applies(spec, true));
        Assert.True(TestPageBlanking.Applies(spec, TimeSpan.Zero));
        Assert.False(TestPageBlanking.Applies(spec, TimeSpan.FromSeconds(5)));
        Assert.True(TestPageBlanking.Applies(spec, DateTime.MinValue));
        Assert.False(TestPageBlanking.Applies(spec, new DateTime(2024, 3, 2)));
        // A string is something BC's decorator parses first; here it is never a number.
        Assert.False(TestPageBlanking.Applies(spec, "0"));
    }

    [Fact]
    public void FormatAndFormatObject_ReadTheSameWayOnBothSides()
    {
        var spec = new TestPageBlanking.Spec(BlankNumbers.BlankZero, false);
        Assert.Equal(string.Empty, TestPageBlanking.Format(Dec(0m), spec));
        Assert.Equal(string.Empty, TestPageBlanking.FormatObject(0m, spec));
        Assert.Null(TestPageBlanking.Format(Dec(5m), spec));
        Assert.Null(TestPageBlanking.FormatObject(5m, spec));
        Assert.Null(TestPageBlanking.Format(Dec(0m), null));
    }

    // ── Date, Time, DateTime: the control's short forms, not AL Format() ─────────────────

    [Theory]
    [InlineData(NavType.Date, "2024-03-02T00:00:00", "3/2/2024")]
    [InlineData(NavType.Date, "2024-12-31T00:00:00", "12/31/2024")]
    [InlineData(NavType.Time, "0001-01-02T12:34:56", "12:34:56 PM")]
    [InlineData(NavType.Time, "0001-01-02T23:59:59", "11:59:59 PM")]
    [InlineData(NavType.Time, "0001-01-02T00:00:01", "12:00:01 AM")]
    [InlineData(NavType.DateTime, "2024-03-02T12:34:56", "3/2/2024 12:34 PM")]   // seconds are not shown
    [InlineData(NavType.DateTime, "2024-03-02T00:00:00", "3/2/2024 12:00 AM")]
    public void TemporalFormatObject_UsesTheShortFormOfItsOwnType(NavType type, string iso, string expected)
        => Assert.Equal(expected, Plain(TestPageTemporalText.FormatObject(
            DateTime.Parse(iso, CultureInfo.InvariantCulture), type)));

    // The space before AM/PM is U+202F where the runtime's culture data is CLDR 42 or newer and
    // U+0020 before it, on BC's tier and here alike; the rows above are written with U+0020.
    private static string? Plain(string? text) => text?.Replace('\u202f', ' ').Replace('\u00a0', ' ');

    [Fact]
    public void TemporalFormatObject_UtcKindDateTime_ReadsAsLocalTime_LikeBcsFormatter()
    {
        // A fixed zone, so the conversion cannot be the identity on a UTC machine.
        var plusFive = TimeZoneInfo.CreateCustomTimeZone("+5", TimeSpan.FromHours(5), "+5", "+5");
        var utc = new DateTime(2024, 3, 2, 12, 34, 56, DateTimeKind.Utc);
        Assert.Equal("3/2/2024 5:34 PM", Plain(TestPageTemporalText.FormatObject(utc, NavType.DateTime, plusFive)));
        // BC's ALSetValue hands a DateTime over already local, and that is never shifted again.
        var local = new DateTime(2024, 3, 2, 12, 34, 56, DateTimeKind.Unspecified);
        Assert.Equal("3/2/2024 12:34 PM", Plain(TestPageTemporalText.FormatObject(local, NavType.DateTime, plusFive)));
        // A Date or a Time carries no zone of its own, so it is never shifted.
        Assert.Equal("3/2/2024", TestPageTemporalText.FormatObject(utc, NavType.Date, plusFive));
    }

    [Theory]
    [InlineData(NavType.Text)]
    [InlineData(NavType.Code)]
    [InlineData(NavType.Integer)]
    [InlineData(NavType.Decimal)]
    public void TemporalFormatObject_NotATemporalControl_Declines(NavType type)
        => Assert.Null(TestPageTemporalText.FormatObject(new DateTime(2024, 3, 2), type));

    [Fact]
    public void TemporalFormatObject_BlankOrNotADateTime_DeclinesToTheBlankArmAndTheFallThrough()
    {
        // default(DateTime) is the blank the blank-temporal arm owns; this arm must not render it.
        Assert.Null(TestPageTemporalText.FormatObject(default(DateTime), NavType.Date));
        Assert.Null(TestPageTemporalText.FormatObject("3/2/2024", NavType.Date));
        Assert.Null(TestPageTemporalText.FormatObject(5, NavType.Date));
        Assert.Null(TestPageTemporalText.Format(null));
        Assert.Null(TestPageTemporalText.Format(Dec(1m)));
    }

    // ── Guid and Duration: AL Format()'s default, with a zero Duration blank ─────────────

    [Fact]
    public void GuidFormatObject_IsBracedUpperCase_AndTheNullGuidIsNotBlank()
    {
        Assert.Equal("{11111111-2222-3333-4444-555555555555}",
            TestPageGuidDurationText.FormatObject(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        Assert.Equal("{00000000-0000-0000-0000-000000000000}",
            TestPageGuidDurationText.FormatObject(Guid.Empty));
        Assert.Equal("{ABCDEF01-2345-6789-ABCD-EF0123456789}",
            TestPageGuidDurationText.FormatObject(Guid.Parse("abcdef01-2345-6789-abcd-ef0123456789")));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(5000, "5 seconds")]
    [InlineData(3723000, "1 hour 2 minutes 3 seconds")]
    [InlineData(86400000, "1 day")]
    [InlineData(1, "1 millisecond")]
    public void DurationFormatObject_IsAlFormatsSpellingAndZeroIsBlank(long milliseconds, string expected)
        => Assert.Equal(expected, TestPageGuidDurationText.FormatObject(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void GuidDurationFormatObject_AnythingElse_Declines()
    {
        Assert.Null(TestPageGuidDurationText.FormatObject("{...}"));
        Assert.Null(TestPageGuidDurationText.FormatObject(5000));
        Assert.Null(TestPageGuidDurationText.FormatObject(null));
        Assert.Null(TestPageGuidDurationText.Format(null));
        Assert.Null(TestPageGuidDurationText.Format(Dec(1m)));
    }

    [Fact]
    public void DurationTryResolve_AcceptsBcsOwnSpelling_AndTheRoundTripOfTheFormatter()
    {
        foreach (var text in new[] { "5 seconds", "1 hour 2 minutes 3 seconds", "1 day 1 hour" })
        {
            Assert.True(TestPageTemporalValue.TryResolve(NavType.Duration, text, out var resolved), text);
            // What the control shows for the value is what was written: the loop closes.
            Assert.Equal(text, TestPageGuidDurationText.Format(resolved));
        }
    }

    [Fact]
    public void DurationTryResolve_AnUnreadableSpelling_DeclinesRatherThanThrowing()
    {
        Assert.False(TestPageTemporalValue.TryResolve(NavType.Duration, "soon", out var resolved));
        Assert.Null(resolved);
    }
}
