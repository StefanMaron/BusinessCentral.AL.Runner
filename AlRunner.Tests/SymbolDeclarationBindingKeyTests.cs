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
//   Both families carry a public Name on the concrete NavFormSourceExpression, which is what
//   makes this a translation rather than a guess.
//
// WHY THE NAME IS READ BY REFLECTION
//   BC exposes it through a type only half the matrix has. Measured against the provisioned
//   artifacts: INavFormSourceExpression is ABSENT on 27.0 and 27.5, PRESENT on 28.1 and 28.4.
//   Naming it fails the BUILD on 27.x -- which is how this arrived, as `error CS0234` on both
//   27.x legs while 28.1 (the local default) compiled fine. The concrete class carries a public
//   `Name` on every version, and RegisterSourceExpression ends in sourceExpressions.Add(id, ...)
//   on 27.0 exactly as it does on 28.1, so the join needs no version-conditional type.
//
//   That makes the fake below deliberately DUCK-TYPED: it implements no BC interface, so these
//   tests exercise the same reflection path 27.x takes. A fake implementing the 28.x interface
//   would have passed here while the shipped code failed to compile on half the matrix.
//
// SCALE. 7,173 of Base Application 28.1's declarations are expression-bound (5,073 control +
// 2,100 action) against 13,053 literal ones, so this path decides roughly a third of what the
// fix newly honours.
using System;
using System.Collections;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class SymbolDeclarationBindingKeyTests
{
    /// <summary>
    /// Stands in for one entry of NavForm.SourceExpressions, carrying just the public Name the
    /// translation joins on. Deliberately implements NO BC interface — see the header: the
    /// shipped code reads Name by reflection precisely because INavFormSourceExpression does
    /// not exist on 27.x, so a fake bound to that interface would test a path 27.x never takes.
    /// </summary>
    private sealed class FakeExpression
    {
        public FakeExpression(string id, string name) { Id = id; Name = name; }
        public string Id { get; }
        public string Name { get; }
    }

    /// <summary>An entry that carries no Name at all — the shape that must be skipped.</summary>
    private sealed class NamelessEntry
    {
        public string Id => "p1p1Nameless";
    }

    private static IDictionary Table(params FakeExpression[] expressions)
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
    public void AnEntryThatCarriesNoName_IsSkippedRatherThanMatched()
    {
        // NavForm.SourceExpressions is a non-generic IDictionary, so nothing guarantees every
        // value carries a Name — a bare string and a Name-less object both appear. Neither may
        // be matched, and neither may stop the walk before the real entry.
        var table = new Hashtable
        {
            ["Control123_DynamicCaption"] = "not an expression",
            ["p1p1Nameless"] = new NamelessEntry(),
            ["p790p790PageEditable"] = new FakeExpression("p790p790PageEditable", "PageEditable"),
        };

        Assert.Equal("p790p790PageEditable",
            RunnerPageInstance.TranslateSymbolDeclarationToBindingKey("PageEditable", table));
    }

    // ── the reflection read itself, which is what makes the join version-portable ──────────

    [Fact]
    public void SourceExpressionNameOrNull_ReadsAPublicNameProperty()
    {
        // The 27.x path. Nothing here implements a BC interface, so a match proves the read
        // works by property name alone.
        Assert.Equal("PageEditable",
            RunnerPageInstance.SourceExpressionNameOrNull(
                new FakeExpression("p790p790PageEditable", "PageEditable")));
    }

    [Fact]
    public void SourceExpressionNameOrNull_AnswersNullForAnEntryWithNoName()
    {
        // Load-bearing, and the reason this is its own method rather than an inline cast: if a
        // BC version renamed the property, this would answer null for EVERY entry and the join
        // would silently become a no-op on that version — reintroducing the exact silent
        // default this change removes, on half the matrix, with nothing to say so. Both arms
        // are asserted so a regression cannot look like an absence.
        Assert.Null(RunnerPageInstance.SourceExpressionNameOrNull(new NamelessEntry()));
        Assert.Null(RunnerPageInstance.SourceExpressionNameOrNull("a bare string"));
        Assert.Null(RunnerPageInstance.SourceExpressionNameOrNull(new object()));
    }
}
