// RunnerPageInstanceLiteralFalseTests — contract tests for
// AlRunner.Patches.RunnerPageInstance.IsLiteralFalse (issue #1838).
//
// This is deliberately NOT a claim about what Business Central does (that claim is proven
// upstream — the two known-gaps-testpage.json entries removed in this same PR, backed by
// StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60961 "Test Page Field Visible Tests"
// at the pinned submodule commit, which already runs against real BC). It is a claim about OUR
// OWN literal-recognition helper: the one piece of #1838's fix that is safe to unit-test without
// a live NavForm/MetadataHelper (ControlIsCompileTimeEliminated itself needs a real compiled
// page's metadata to walk, which only the corpus/probe runs above exercise).
//
// The distinction this helper exists to enforce is the whole point of the fix: a control whose
// Visible is the compile-time LITERAL false is dead-code-eliminated (TestPage access must raise
// "is not found on the page"), but a control whose Visible is bound to an EXPRESSION NAME — even
// one that currently evaluates false — must stay reachable (that is #1778's live-evaluation
// territory, untouched here). IsLiteralFalse must say yes only to the literal spellings and no to
// everything else, including a page-variable expression name that happens to collide in shape.
//
// RED/GREEN: reverting IsLiteralFalse to `=> false` (never eliminate) makes
// ControlIsCompileTimeEliminated never fire, reproducing #1838's original bug (both corpus tests
// fail with "An error was expected inside an ASSERTERROR statement"). Reverting it to `=> true`
// (always eliminate) would instead eliminate every control, including live-expression and
// always-visible ones — this file pins both directions so a regression in the helper itself is
// caught here, in milliseconds, without needing the BC engine loaded.
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerPageInstanceLiteralFalseTests
{
    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("0")]
    public void IsLiteralFalse_LiteralFalseSpelling_ReturnsTrue(string raw)
    {
        Assert.True(RunnerPageInstance.IsLiteralFalse(raw));
    }

    // Negative: a literal true, an absent property, and — the load-bearing case — an
    // EXPRESSION NAME (what a `Visible = ShowDynamic;` property is actually spelled as in the
    // emitted metadata) must never be treated as an eliminating literal, even though some of
    // these strings look superficially similar to the literal forms above.
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ShowDynamic")]
    [InlineData("p60959p60959ShowOuter")]
    [InlineData("falsey")]
    public void IsLiteralFalse_AnythingElse_ReturnsFalse(string? raw)
    {
        Assert.False(RunnerPageInstance.IsLiteralFalse(raw));
    }
}
