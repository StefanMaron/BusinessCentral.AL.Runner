// TestPageNumericValueTests — contract tests for AlRunner.TestPageNumericValue (issue #3406).
//
// These are claims about OUR OWN conversion helper, not about Business Central. The BC claim —
// what a real service tier renders for a Decimal control carrying AutoFormatType,
// AutoFormatExpression or DecimalPlaces — is asked upstream in
// StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60605 "ALT AutoFormat Tests" (corpus
// PR #277), where eight BC legs adjudicate it. Nothing here asserts what BC renders.
//
// What this file pins is the mechanism the helper now uses, and why it is not a guess:
// BC's own NavForm.GetDecimalString cascade already runs to completion inside the runner and
// publishes its result as the control's "Control<id>_Format" source expression. Measured on
// BC 28.1 against a four-control probe page, all four format strings computed by BC's own code:
//
//     AutoFormatType = 0                                  -> #,##0.00
//     AutoFormatType = 1,  expression 'EUR'               -> #,##0.00
//     AutoFormatType = 10, expression '<Precision,3:3>…'  -> #,##0.000
//     DecimalPlaces  = 3 : 3                              -> #,##0.000
//
// So the format string is BC's answer, not the runner's, and the helper's job is only to apply
// it rather than to invent one. Before this change the helper ignored it and hardcoded "0.00"
// for every Decimal on every page, which is why AutoFormatType = 10 and DecimalPlaces = 3 : 3
// both read back as two decimals.
using System.Globalization;
using AlRunner;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageNumericValueTests
{
    private static NavDecimal Dec(decimal value) => (NavDecimal)NavDecimal.Create((Decimal18)value);

    // ── the format string is applied, not ignored ────────────────────────────

    [Theory]
    // The four format strings BC's own cascade produced for the probe page, each paired with
    // the digits it must produce. The 3-decimal rows are the ones the hardcoded "0.00" got
    // wrong, and they differ from the 2-decimal rows in the LAST digit, so a helper that
    // silently kept two decimals cannot pass them.
    [InlineData("#,##0.00", "1,234.50")]
    [InlineData("#,##0.000", "1,234.500")]
    // A format asking for no decimals at all: rounds rather than truncates, and proves the
    // helper is not simply appending a fixed fractional part.
    [InlineData("#,##0", "1,235")]
    // No grouping separator requested: the helper must not add one of its own.
    [InlineData("0.00", "1234.50")]
    [InlineData("0.000", "1234.500")]
    public void Format_WithAControlFormat_AppliesThatFormat(string controlFormat, string expected)
    {
        Assert.Equal(expected, TestPageNumericValue.Format(Dec(1234.5m), controlFormat));
    }

    [Fact]
    public void Format_WithAControlFormat_DiffersFromTheTwoDecimalDefault()
    {
        // The point of the change, stated as a comparison rather than as two separate
        // constants: a three-decimal control and a two-decimal control must not read alike.
        var twoDecimals   = TestPageNumericValue.Format(Dec(1234.5m), "#,##0.00");
        var threeDecimals = TestPageNumericValue.Format(Dec(1234.5m), "#,##0.000");

        Assert.NotEqual(twoDecimals, threeDecimals);
        Assert.Equal("1,234.50",  twoDecimals);
        Assert.Equal("1,234.500", threeDecimals);
    }

    // ── the fallback, for a control with no page behind it ───────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Format_WithNoControlFormat_KeepsTheHistoricalTwoDecimalSpelling(string? noFormat)
    {
        // A record-only field (LiveNavTestField's two-argument constructor) has no page and so
        // no format expression to read. That path is unchanged by this fix and must stay so:
        // every existing assertion in the corpus and in Microsoft's own test buckets was
        // written against this spelling.
        Assert.Equal("1234.50", TestPageNumericValue.Format(Dec(1234.5m), noFormat));
    }

    // ── a malformed format string must not silently answer something else ────

    [Fact]
    public void Format_WithAnUnusableFormat_FallsBackRatherThanThrowing()
    {
        // .NET treats an unrecognised custom format as a literal, which would answer the
        // format string itself where a number belongs — a silent wrong answer of exactly the
        // kind this issue is about. The helper detects that its output carries no digit and
        // falls back to the historical spelling instead.
        Assert.Equal("1234.50", TestPageNumericValue.Format(Dec(1234.5m), "not-a-format"));
    }

    // ── non-decimals are still declined, so the caller falls through ─────────

    [Fact]
    public void Format_NonDecimal_DeclinesSoTheCallerCanTryTheNextHelper()
    {
        // Format is one arm of a ?? chain in LiveNavTestField.Value: Option, then numeric,
        // then Boolean, then blank-temporal. Answering non-null for a Boolean here would
        // steal the read from TestPageBooleanValue and reintroduce "True"/"False" (#2795).
        Assert.Null(TestPageNumericValue.Format(NavBoolean.Create(true), "#,##0.00"));
        Assert.Null(TestPageNumericValue.Format(null, "#,##0.00"));
    }

    // ── negative and zero values keep the format's own sign handling ─────────

    [Theory]
    [InlineData(-1234.5, "#,##0.000", "-1,234.500")]
    [InlineData(0, "#,##0.00", "0.00")]
    public void Format_SignAndZero_FollowTheFormatString(double value, string controlFormat, string expected)
    {
        Assert.Equal(expected,
            TestPageNumericValue.Format(Dec((decimal)value), controlFormat));
    }

    // ── the invariant culture is the one used, whatever the machine's is ─────

    [Fact]
    public void Format_UsesInvariantCulture_NotTheAmbientOne()
    {
        // A German ambient culture would render "1.234,500" if the helper used CurrentCulture.
        // CI legs and developer machines differ here, so pin it rather than inherit it.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1,234.500", TestPageNumericValue.Format(Dec(1234.5m), "#,##0.000"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
