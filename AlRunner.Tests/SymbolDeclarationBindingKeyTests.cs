// The two spellings of one expression, and the translation between them (#3504, #2460).
//
// THE DEFECT THIS PINS
//   Compiled page metadata names an expression by its Id — "p790p790PageEditable" — and BC keys
//   the live binding table on exactly that. From NavForm.RegisterSourceExpression, decompiled
//   from Microsoft.Dynamics.Nav.Ncl.dll 28.1:
//
//       sourceExpressions.Add(expression.Id, expression);
//
//   SymbolReference.json states the raw AL identifier instead — "PageEditable" — which is a
//   different string and matches no key. Feeding it straight to EvaluateProperty turned a
//   resolvable property into a hard refusal: Base Application 790 "G/L Account Categories"
//   declares `Enabled = PageEditable` on five actions (New, MoveUp, MoveDown, Indent, Outdent),
//   and invoking one raised
//
//       'PageEditable' is not a name the page publishes a binding for
//
//   where real BC evaluates the page global and runs the OnAction. That page's own AL, read
//   from the shipped app's src/Finance/GeneralLedger/Account/GLAccountCategories.Page.al,
//   assigns it in two triggers:
//
//       trigger OnOpenPage()           begin ... PageEditable := CurrPage.Editable; end;
//       trigger OnAfterGetCurrRecord() begin PageEditable := CurrPage.Editable; end;
//       var PageEditable: Boolean;
//
//   so on a page opened with OpenEdit() it is true and the action is enabled. Refusing was
//   neither faithful nor necessary — the binding was published all along, under its Id.
//
//   INavFormSourceExpression carries BOTH Id and Name, which is what makes this a translation
//   rather than a guess.
//
// SCALE. 7,173 of Base Application 28.1's declarations are expression-bound (5,073 control +
// 2,100 action) against 13,053 literal ones, so this path decides roughly a third of what the
// fix newly honours.
using System;
using System.Collections;
using System.Threading.Tasks;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class SymbolDeclarationBindingKeyTests
{
    /// <summary>
    /// Stands in for one entry of NavForm.SourceExpressions. Only Id and Name are read by the
    /// translation, but the real interface is implemented rather than a duck-typed stand-in:
    /// the translation filters on `is INavFormSourceExpression`, so a fake that did not
    /// implement it would be skipped and every test here would pass for the wrong reason.
    /// </summary>
    private sealed class FakeExpression : INavFormSourceExpression
    {
        public FakeExpression(string id, string name) { Id = id; Name = name; }

        public string Id { get; }
        public string Name { get; }

        public bool IsInRepeaterGroup { get; set; }
        public Microsoft.Dynamics.Nav.Types.Metadata.SourceExpressionType SourceExpressionType => default;
        public bool SkipOnSaveValues { get; set; }
        public string AutoFormatCurrencyColumnId { get; set; } = string.Empty;
        public Type Type => typeof(bool);
        public bool Writable => false;

        public NavValue Get() => NavBoolean.Create(true);
        public ValueTask<NavValue> GetAsync() => new(Get());
        public void Set(NavValue value) { }
        public ValueTask SetAsync(NavValue value) => default;
    }

    private static IDictionary Table(params INavFormSourceExpression[] expressions)
    {
        var table = new Hashtable();
        foreach (var e in expressions) table[e.Id] = e;
        return table;
    }

    private static IDictionary Page790() =>
        Table(new FakeExpression("p790p790PageEditable", "PageEditable"));

    // ── the whole RED ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RawAlIdentifier_TranslatesToTheRegisteredId()
    {
        // Page 790's exact shape. The symbol file says "PageEditable"; the binding table is
        // keyed "p790p790PageEditable"; EvaluateProperty must be handed the second.
        Assert.Equal("p790p790PageEditable",
            RunnerPageInstance.TranslateSymbolDeclarationToBindingKey("PageEditable", Page790()));
    }

    [Fact]
    public void AlIdentifiersAreCaseInsensitive_SoASpellingDifferenceStillResolves()
    {
        // AL identifiers are case-insensitive, so two spellings of one variable are one binding
        // — the same rule DependencyControlsSharingSourceExpression applies to its dedup key.
        Assert.Equal("p790p790PageEditable",
            RunnerPageInstance.TranslateSymbolDeclarationToBindingKey("pageeditable", Page790()));
    }

    [Fact]
    public void AnIdTheTableAlreadyHolds_IsReturnedUnchanged()
    {
        // A page the runner compiled itself already states the Id, so the translation must be a
        // no-op there rather than searching by Name and finding something else. This is what
        // keeps the source-compiled path untouched.
        Assert.Equal("p790p790PageEditable",
            RunnerPageInstance.TranslateSymbolDeclarationToBindingKey("p790p790PageEditable", Page790()));
    }

    // ── negatives: the honest refusal must survive ────────────────────────────────────────

    [Fact]
    public void ANameNothingPublishes_IsReturnedUnchanged_SoTheRefusalStaysHonest()
    {
        // The load-bearing negative. A name the page genuinely does not publish must come back
        // AS IT WAS, so EvaluateProperty raises its refusal naming what the AL actually wrote.
        // Returning null or "" here would instead route it into the AL0573 dropped-expression
        // arm or the AL-default-true arm, both of which would answer a question nobody measured.
        Assert.Equal("NoSuchGlobal",
            RunnerPageInstance.TranslateSymbolDeclarationToBindingKey("NoSuchGlobal", Page790()));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("1")]
    public void ALiteral_IsNeverTranslated(string literal)
    {
        // 13,053 of Base Application 28.1's declarations are literals. EvaluateProperty owns the
        // literal-vs-expression decision, so a literal must pass through untouched — and must
        // not be able to collide with an expression NAME either. Asserted against a table whose
        // expression is deliberately NAMED "false" to prove the literal check runs first.
        var table = Table(new FakeExpression("p1p1Trap", "false"));
        Assert.Equal(literal, RunnerPageInstance.TranslateSymbolDeclarationToBindingKey(literal, table));
    }

    [Fact]
    public void NullAndEmpty_ArePreservedExactly()
    {
        // These two mean different things downstream — null is "declares none" (AL default
        // true), empty is the AL0573 expression the compiler dropped — so neither may be
        // collapsed into the other, or into a lookup.
        Assert.Null(RunnerPageInstance.TranslateSymbolDeclarationToBindingKey(null, Page790()));
        Assert.Equal("", RunnerPageInstance.TranslateSymbolDeclarationToBindingKey("", Page790()));
    }

    [Fact]
    public void AnEntryThatIsNotASourceExpression_IsSkippedRatherThanMatched()
    {
        // NavForm.SourceExpressions is a non-generic IDictionary, so nothing guarantees every
        // value implements the interface. A non-conforming entry must be skipped, not cast.
        var table = new Hashtable
        {
            ["Control123_DynamicCaption"] = "not an expression",
            ["p790p790PageEditable"] = new FakeExpression("p790p790PageEditable", "PageEditable"),
        };

        Assert.Equal("p790p790PageEditable",
            RunnerPageInstance.TranslateSymbolDeclarationToBindingKey("PageEditable", table));
    }
}
