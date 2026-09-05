// TestPageBooleanValueTests — contract tests for AlRunner.TestPageBooleanValue (issues #1837
// and #2795 — see MockTestPage.cs's TestPageBooleanValue doc comment for the full story).
//
// These are claims about OUR OWN conversion helper, not about Business Central. The BC claim —
// that a Boolean control reads 'Yes'/'No', that AssertEquals(<Boolean>) agrees with that
// spelling, and which text spellings a write accepts — is pinned upstream in
// StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60666 "TPB Bool Tests", alongside the
// already-merged BooleanFieldControl_ReadsAsYesOrNo which measured the read on all eight BC
// legs.
//
// This file exists so a regression in the helper is caught in milliseconds, without the BC
// engine loaded.
//
// NOTE ON WHAT CHANGED IN #2795. An earlier version of this file asserted that "Yes" and "No"
// must THROW, on the reasoning that the only spelling a SetValue(<Boolean>) round trip can
// produce is what ValueToString emits, which was Convert.ToString(bool) — "True"/"False". The
// premise was right and the value was wrong: real BC's ValueToString for a Boolean control
// emits "Yes"/"No", so those are exactly the spellings the round trip carries, and refusing
// them refused BC's own. That is why the negative cases below are now the genuinely unknown
// spellings only.
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageBooleanValueTests
{
    // ── rendering ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void Format_NavBoolean_RendersTheWordBcRendersIt(bool value, string expected)
    {
        Assert.Equal(expected, TestPageBooleanValue.Format(NavBoolean.Create(value)));
    }

    [Theory]
    [InlineData(true, "Yes")]
    [InlineData(false, "No")]
    public void FormatObject_ClrBoolean_RendersTheSameWord(bool value, string expected)
    {
        // ValueToString sees the unwrapped CLR value, the Value getter sees the NavValue. Both
        // must land on one word or BC's ALAssertEquals — which converts the expected Boolean
        // through ValueToString and compares it ORDINALLY against the control's Value — can
        // never match.
        Assert.Equal(expected, TestPageBooleanValue.FormatObject(value));
        Assert.Equal(TestPageBooleanValue.Format(NavBoolean.Create(value)),
                     TestPageBooleanValue.FormatObject(value));
    }

    /// <summary>
    /// Both formatters must decline anything that is not a Boolean, so the Value getters fall
    /// through to their next case instead of this one swallowing every other type.
    /// </summary>
    [Fact]
    public void Format_NonBoolean_DeclinesSoTheCallerFallsThrough()
    {
        Assert.Null(TestPageBooleanValue.Format(NavText.CreateTruncated(10, "Yes")));
        Assert.Null(TestPageBooleanValue.Format(NavInteger.Create(1)));
        Assert.Null(TestPageBooleanValue.Format(null));
        Assert.Null(TestPageBooleanValue.FormatObject("Yes"));
        Assert.Null(TestPageBooleanValue.FormatObject(1));
        Assert.Null(TestPageBooleanValue.FormatObject(null));
    }

    // ── the inverse ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("No", false)]
    [InlineData("no", false)]
    [InlineData("NO", false)]
    public void Resolve_TheSpellingTheControlReadsAs_ReturnsMatchingNavBoolean(string input, bool expected)
    {
        // #2795: this is the spelling ValueToString now produces, so a SetValue(<Boolean>) round
        // trip comes back in through here. Refusing it — as this helper used to — made
        // SetValue(true) impossible to satisfy once the rendering was corrected.
        var boolean = Assert.IsType<NavBoolean>(TestPageBooleanValue.Resolve(input, "unit test"));
        Assert.Equal(expected, boolean.Value);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void Resolve_TrueFalseSpelling_IsStillAccepted(string input, bool expected)
    {
        // Kept deliberately: it is the spelling AL's own Evaluate accepts for a Boolean, and a
        // test writing it works on this runner today. Whether real BC accepts it on the TestPage
        // surface is in front of a service tier as
        // TestPageField_SetValue_TrueFalseSpelling_IsAlsoAccepted in the corpus suite; dropping
        // it here on a guess would regress a working surface while fixing an unrelated one.
        var boolean = Assert.IsType<NavBoolean>(TestPageBooleanValue.Resolve(input, "unit test"));
        Assert.Equal(expected, boolean.Value);
    }

    /// <summary>
    /// The negative direction, and it still matters: a spelling neither the control's own
    /// rendering nor AL's Evaluate produces must refuse LOUDLY rather than default to false.
    /// A silent false here would make an assertion about a Boolean control pass for the wrong
    /// reason, which is the failure mode loud-failures.md exists for.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("Ja")]
    [InlineData("Blorp")]
    [InlineData("")]
    public void Resolve_AnUnknownSpelling_ThrowsOutOfScope_NamingTheReason(string input)
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => TestPageBooleanValue.Resolve(input, "unit test context"));

        Assert.Contains("testpage-boolean-value", ex.Reason);
        Assert.Contains("unit test context", ex.Message);
    }
}
