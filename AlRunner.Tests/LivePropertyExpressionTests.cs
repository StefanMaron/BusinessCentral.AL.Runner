// LivePropertyExpressionTests — the C# half of issue #3730.
//
// The AL-observable claim (an action's Enabled, and a control's Enabled/Editable, bound to a
// source-table field follow the CURRENT ROW, while a control's Visible does not) belongs to real
// BC and is adjudicated upstream by corpus codeunit 60436 "TPAE Tests".
//
// What is provable here without a loaded BC runtime is the C# contract that fix rests on: the
// narrowing RunnerPageInstance applies to a field's ClientObject before it reaches
// PageControlExpression, and the fact that PageControlExpression really does evaluate the three
// shapes the corpus arms use once a resolver answers field names at all — including an
// Option compared against ORDINALS, which is how the AL compiler writes such a comparison into
// the page metadata.
using System;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public class LivePropertyExpressionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData("Filled")]
    [InlineData("")]
    [InlineData(1)]
    [InlineData(0)]
    public void TryComparableFieldValue_PassesThroughAShapeCompareCanOrder(object clientObject)
    {
        Assert.True(RunnerPageInstance.TryComparableFieldValue(clientObject, out var value));
        Assert.Equal(clientObject, value);
    }

    [Fact]
    public void TryComparableFieldValue_PassesThroughDecimalAndTheOtherNumericWidths()
    {
        Assert.True(RunnerPageInstance.TryComparableFieldValue(12.5m, out var dec));
        Assert.Equal(12.5m, dec);

        Assert.True(RunnerPageInstance.TryComparableFieldValue((long)7, out var lng));
        Assert.Equal((long)7, lng);
    }

    // The negative direction, and the one that matters: a shape the evaluator's Compare has no
    // ordering for must be REFUSED, so EvaluateProperty raises naming the expression rather than
    // handing Compare an operand it would have to invent an answer for.
    [Fact]
    public void TryComparableFieldValue_RefusesAShapeCompareCannotOrder()
    {
        Assert.False(RunnerPageInstance.TryComparableFieldValue(Guid.NewGuid(), out var guid));
        Assert.Null(guid);

        Assert.False(RunnerPageInstance.TryComparableFieldValue(new object(), out var opaque));
        Assert.Null(opaque);

        Assert.False(RunnerPageInstance.TryComparableFieldValue(null, out var missing));
        Assert.Null(missing);
    }

    // The three property texts the corpus arms produce, evaluated through the real parser with a
    // resolver that answers field names — the composition the #3730 fix creates. The metadata
    // spellings here are the compiler's own (see PageControlExpression's file header): a bare
    // field name for a Boolean, a quoted comparison for Text, and ORDINALS for an Option.
    [Theory]
    [InlineData("Flag", true, true)]
    [InlineData("Flag", false, false)]
    public void ABareBooleanFieldNameEvaluatesToTheFieldsValue(string text, bool flag, bool expected)
    {
        Assert.True(PageControlExpression.TryEvaluateBoolean(
            text, Resolver(flag, "Filled", 1), out var evaluated, out var why), why);
        Assert.Equal(expected, evaluated);
    }

    [Theory]
    [InlineData("Filled", true)]
    [InlineData("", false)]
    public void ATextComparisonEvaluatesAgainstTheFieldsValue(string value, bool expected)
    {
        Assert.True(PageControlExpression.TryEvaluateBoolean(
            "Value <> ''", Resolver(true, value, 1), out var evaluated, out var why), why);
        Assert.Equal(expected, evaluated);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public void AnOptionComparisonEvaluatesAgainstTheFieldsOrdinal(int ordinal, bool expected)
    {
        Assert.True(PageControlExpression.TryEvaluateBoolean(
            "( LineType = 1 ) or ( LineType = 2 )",
            Resolver(true, "Filled", ordinal), out var evaluated, out var why), why);
        Assert.Equal(expected, evaluated);
    }

    // And the refusal survives the composition: a field the resolver does not answer must still
    // come back as a failure, never as a default, so #3693's Invoke()-time Enabled check cannot
    // silently invent an answer for a shape nobody has measured.
    [Fact]
    public void AnUnresolvedIdentifierIsStillAFailure()
    {
        Assert.False(PageControlExpression.TryEvaluateBoolean(
            "Unknown", Resolver(true, "Filled", 1), out _, out var why));
        Assert.Contains("Unknown", why);
    }

    private static PageControlExpression.ResolveIdentifier Resolver(bool flag, string value, int lineType)
        => (string name, bool quoted, out object? resolved) =>
        {
            object? raw = name switch
            {
                "Flag" => flag,
                "Value" => value,
                "LineType" => lineType,
                _ => null,
            };
            if (raw == null) { resolved = null; return false; }
            return RunnerPageInstance.TryComparableFieldValue(raw, out resolved);
        };
}
