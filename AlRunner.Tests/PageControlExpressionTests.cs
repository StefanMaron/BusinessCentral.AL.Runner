// Unit tests for the client-expression parser behind a page control's Visible/Editable/Enabled.
//
// These are runner-local on purpose. What BC ANSWERS for `Visible = not Flag` is BC behavior and
// is adjudicated upstream by the corpus against a real service tier. What the AL compiler WRITES
// into the page metadata, and how this runner reads it back, is a runner-internal claim that no
// service tier can settle — which is what these pin.
//
// Every literal expression string below was measured, not invented: it is the text the BC 28.1 AL
// compiler wrote into the metadata for the AL quoted beside it. The mangled `p65901p65901X` names
// are page globals on page 65901; the bare names are fields on its source table.

using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class PageControlExpressionTests
{
    /// <summary>
    /// A resolver over a fixed set of names, standing in for the page's source-expression table
    /// and its source record. Anything not listed resolves to nothing, which is what makes the
    /// unresolved-name tests below mean something.
    /// </summary>
    private static PageControlExpression.ResolveIdentifier Resolver(
        params (string Name, object? Value)[] known)
        => (string name, bool quoted, out object? value) =>
        {
            foreach (var (n, v) in known)
                if (n == name) { value = v; return true; }
            value = null;
            return false;
        };

    private static readonly PageControlExpression.ResolveIdentifier Page = Resolver(
        ("p65901p65901HideIt", false),
        ("p65901p65901LockIt", false),
        ("p65901p65901Flag2", true),
        ("Flag", true),
        ("Value", "v"),
        ("Qty", 5m),
        ("Kind", 1),
        ("Spaced Name", true),
        ("NotABool", "some text"));

    private static bool Eval(string text, PageControlExpression.ResolveIdentifier? resolve = null)
    {
        var ok = PageControlExpression.TryEvaluateBoolean(
            text, resolve ?? Page, out var value, out var failure);
        Assert.True(ok, $"'{text}' should evaluate, but failed: {failure}");
        return value;
    }

    private static string Failure(string text, PageControlExpression.ResolveIdentifier? resolve = null)
    {
        var ok = PageControlExpression.TryEvaluateBoolean(
            text, resolve ?? Page, out _, out var failure);
        Assert.False(ok, $"'{text}' should NOT evaluate, but it returned a value");
        Assert.NotNull(failure);
        return failure!;
    }

    // ---- the measured shapes ------------------------------------------------------------------

    [Theory]
    // AL: Visible = not HideIt            (HideIt is false, so this is true)
    [InlineData("not p65901p65901HideIt", true)]
    // AL: Visible = HideIt and LockIt     (both false)
    [InlineData("p65901p65901HideIt and p65901p65901LockIt", false)]
    // AL: Visible = HideIt or Flag2       (Flag2 is true)
    [InlineData("p65901p65901HideIt or p65901p65901Flag2", true)]
    // AL: Visible = not (HideIt or LockIt)
    [InlineData("not ( p65901p65901HideIt or p65901p65901LockIt )", true)]
    // AL: Visible = Rec.Flag              (a source-table field, not a page global)
    [InlineData("Flag", true)]
    // AL: Visible = not Rec.Flag
    [InlineData("not Flag", false)]
    // AL: Visible = Rec.Value <> ''
    [InlineData("Value <> ''", true)]
    // AL: Visible = Rec."Spaced Name"     (a name that needed AL quotes keeps them)
    [InlineData("\"Spaced Name\"", true)]
    // AL: Visible = Rec.Qty > 0
    [InlineData("Qty > 0", true)]
    // AL: Visible = Rec.Kind = Rec.Kind::Second   (the enum member arrives as its ORDINAL)
    [InlineData("Kind = 1", true)]
    // AL: Visible = (Rec.Value = 'x') or Flag2
    [InlineData("( Value = 'x' ) or p65901p65901Flag2", true)]
    [InlineData("( Value = 'x' ) or p65901p65901LockIt", false)]
    // AL: Visible = Rec.Qty > 1 + 1       (arithmetic does appear)
    [InlineData("Qty > 1 + 1", true)]
    [InlineData("Qty > 4 + 4", false)]
    public void MeasuredShapes_EvaluateToTheirAlAnswer(string text, bool expected)
        => Assert.Equal(expected, Eval(text));

    // ---- precedence ---------------------------------------------------------------------------

    // AL's precedence is Pascal's, not C's: `and` binds like multiplication, `or` like addition,
    // and the comparison operators bind LOOSEST. So `A = B and C` is `A = (B and C)`.
    //
    // The operands are chosen so the two readings disagree. HideIt is false, Flag2 true, LockIt
    // false:
    //   AL:      false = (true and false)  ->  false = false  ->  TRUE
    //   C-style: (false = true) and false  ->  false and false ->  false
    [Fact]
    public void Comparison_BindsLooserThanAnd_AsInAl()
        => Assert.True(Eval("p65901p65901HideIt = p65901p65901Flag2 and p65901p65901LockIt"));

    // `not` binds tighter than `and`. Both operands false, so the two readings disagree:
    //   tight: (not false) and false  ->  true and false  ->  FALSE
    //   loose: not (false and false)  ->  not false       ->  true
    [Fact]
    public void Not_BindsTighterThanAnd()
        => Assert.False(Eval("not p65901p65901HideIt and p65901p65901LockIt"));

    // `and` binds tighter than `or`: `false and false or true` is `(false and false) or true` =
    // true, while `false and (false or true)` would be false.
    [Fact]
    public void And_BindsTighterThanOr()
        => Assert.True(Eval("p65901p65901HideIt and p65901p65901LockIt or p65901p65901Flag2"));

    // ---- operators ----------------------------------------------------------------------------

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    [InlineData("not true", false)]
    [InlineData("true xor true", false)]
    [InlineData("true xor false", true)]
    [InlineData("1 = 1", true)]
    [InlineData("1 <> 1", false)]
    [InlineData("2 > 1", true)]
    [InlineData("2 >= 2", true)]
    [InlineData("1 < 2", true)]
    [InlineData("2 <= 1", false)]
    [InlineData("7 div 2 = 3", true)]
    [InlineData("7 mod 2 = 1", true)]
    [InlineData("6 / 2 = 3", true)]
    [InlineData("2 * 3 = 6", true)]
    [InlineData("5 - 3 = 2", true)]
    [InlineData("- 3 < 0", true)]
    public void Operators_ProduceTheirAlAnswer(string text, bool expected)
        => Assert.Equal(expected, Eval(text));

    // AL compares Text and Code case-insensitively, so 'v' = 'V' is true. An ordinal comparison
    // would answer false here and diverge from BC on exactly the shape these properties take.
    [Fact]
    public void StringComparison_IsCaseInsensitive_AsInAl()
        => Assert.True(Eval("Value = 'V'"));

    // The AL string literal escape is a doubled quote.
    [Fact]
    public void StringLiteral_ReadsADoubledQuoteAsOne()
        => Assert.True(Eval("'it''s' = 'IT''S'"));

    // ---- failures are loud, never a default ---------------------------------------------------

    // The whole point of the surrounding code: an expression that cannot be evaluated must be
    // reported as a failure so the caller raises RunnerOutOfScopeException. Answering "true" for
    // a Visible we could not compute would make every test of a page's contract unfailable.

    [Fact]
    public void UnresolvedName_Fails_AndNamesIt()
    {
        var failure = Failure("not p65901p65901Missing");
        Assert.Contains("p65901p65901Missing", failure);
        Assert.Contains("source record", failure);
    }

    [Fact]
    public void NonBooleanResult_Fails_AndSaysWhatItWas()
    {
        var failure = Failure("NotABool");
        Assert.Contains("some text", failure);
        Assert.Contains("not a Boolean", failure);
    }

    [Fact]
    public void NotAppliedToANonBoolean_Fails()
        => Assert.Contains("not a Boolean", Failure("not Qty"));

    [Fact]
    public void AndAppliedToNonBooleans_Fails()
        => Assert.Contains("not both Boolean", Failure("Qty and Qty"));

    [Fact]
    public void UnclosedParenthesis_Fails()
        => Assert.Contains("not closed", Failure("not ( Flag"));

    [Fact]
    public void TrailingText_Fails()
        => Assert.Contains("unparsed text", Failure("Flag Flag"));

    [Fact]
    public void EmptyText_Fails()
        => Assert.Contains("empty", Failure("   "));

    [Fact]
    public void UnterminatedStringLiteral_Fails()
        => Assert.Contains("unterminated string", Failure("Value = 'x"));

    [Fact]
    public void CharacterOutsideTheGrammar_Fails()
        => Assert.Contains("not part of the client-expression grammar", Failure("Flag & Flag"));

    [Fact]
    public void DivisionByZero_Fails()
        => Assert.Contains("divide by zero", Failure("Qty / 0 = 1"));

    // A procedure call cannot reach here — the AL compiler rejects one in a client expression with
    // AL0322 — but if the metadata ever carried one, it must fail rather than resolve to a default.
    [Fact]
    public void ProcedureCallShape_Fails()
        => Assert.False(PageControlExpression.TryEvaluateBoolean(
            "IsShown ( )", Page, out _, out _));
}
