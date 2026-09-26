// DateInitValueTests — #4633.
//
// A Date field's InitValue reaches the derived metatable as the AL literal (`20260101D`) from
// both the source parser and SymbolReference.json, and BC evaluates it with format 9, which
// refuses that spelling. The end-to-end proof is corpus codeunit 67510 (a tableextension on Job
// takes the derived route); these cases pin the conversion to what BC's own metadata emitter
// writes, including the spellings that must be left alone.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class DateInitValueTests
{
    [Theory]
    [InlineData("20260101D", "2026-01-01")]
    [InlineData("20240229D", "2024-02-29")]   // leap day
    [InlineData("99991231D", "9999-12-31")]
    [InlineData(" 20260101d ", "2026-01-01")]
    public void AlDateLiteral_BecomesInvariantXmlDate(string input, string expected)
    {
        Assert.True(RecordPatches.TryNormalizeDateInitValue(input, out var result),
            $"expected '{input}' to be recognised as a Date InitValue");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("0D")]
    [InlineData("0d")]
    public void ZeroDate_MeansNoInitValue(string input)
    {
        // BC's emitter omits the property for 0D, so the builder must pass no InitValue at all.
        Assert.True(RecordPatches.TryNormalizeDateInitValue(input, out var result));
        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("C20260101D")]   // closing date: BC's emitter writes it unchanged
    [InlineData("20230229D")]    // not a calendar date
    [InlineData("20261301D")]
    [InlineData("2026011D")]     // seven digits
    [InlineData("20260101")]     // no D suffix
    [InlineData("2026-01-01")]   // already format 9
    [InlineData("")]
    [InlineData("D")]
    [InlineData("today")]
    public void UnrecognisedOrAlreadyCorrect_IsLeftForBcToEvaluate(string input)
        => Assert.False(RecordPatches.TryNormalizeDateInitValue(input, out _),
            "an unrecognised spelling must reach BC's own evaluator unchanged");
}
