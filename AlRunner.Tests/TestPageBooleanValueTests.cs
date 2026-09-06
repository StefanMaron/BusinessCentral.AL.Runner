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
// them refused BC's own.
//
// The mirror of that also turned out to be true, and it took a service tier to settle it. A
// first draft of the fix ACCEPTED "True"/"False" as well, reasoning that it is the spelling AL's
// own Evaluate takes. Corpus PR #163 asked, and all eight BC legs answered with a refusal:
//
//   Validation error for Field: RecTrue,  Message = 'Your entry of 'False' is not an acceptable
//   value for 'Rec True'. (Select Refresh to discard errors)'
//
// So BC has a defined answer here and it is "no". That makes it a validation error to reproduce,
// not an unsupported surface to refuse as out of scope — which is why the negative assertions
// below check for that message rather than for a RunnerOutOfScopeException.
//
// NOTE ON WHAT CHANGED IN #2900. Resolve now raises only the INNER half of that message. The
// "Validation error for Field: <name>,  Message = '…'" wrapper is composed one layer out, by
// BC's own NavTestField.CheckError, from the refusal TestFieldValidationErrors records — so the
// AL-visible string all eight legs measured is unchanged, and the assertions below moved down to
// the layer this helper actually owns. TestFieldValidationErrorsTests pins the other half.
using AlRunner;
using Microsoft.Dynamics.Nav.Types.Exceptions;
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
    [InlineData("True")]
    [InlineData("true")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void Resolve_TrueFalseSpelling_IsRefusedTheWayBcRefusesIt(string input)
    {
        // Measured on all eight BC legs via corpus PR #163, not derived: BC rejects this spelling
        // on a Boolean control as an ordinary field validation error. Accepting it would make the
        // runner take a write a service tier refuses — the silent-divergence shape that is worse
        // than a loud gap, because a test written against the runner would then fail upstream.
        var ex = Assert.Throws<NavNCLDialogException>(
            () => TestPageBooleanValue.Resolve(input, "Rec True"));

        Assert.Contains("is not an acceptable value", ex.Message);
        Assert.Contains(input, ex.Message);
        Assert.Contains("Rec True", ex.Message);
    }

    /// <summary>
    /// Anything else is refused the same way, for the same reason: BC answers a bad entry with a
    /// validation error naming the control, so the runner must too rather than defaulting to
    /// false. A silent false here would make an assertion about a Boolean control pass for the
    /// wrong reason.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("Ja")]
    [InlineData("Blorp")]
    [InlineData("")]
    public void Resolve_AnUnacceptableSpelling_RaisesBcsValidationError(string input)
    {
        var ex = Assert.Throws<NavNCLDialogException>(
            () => TestPageBooleanValue.Resolve(input, "Rec True"));

        // The bare inner message, exactly — the wrapper is BC's (#2900), so asserting it here
        // would pin a string this helper no longer produces.
        Assert.Equal(
            $"Your entry of '{input}' is not an acceptable value for 'Rec True'. "
            + "(Select Refresh to discard errors)",
            ex.Message);
        Assert.DoesNotContain("Validation error for Field", ex.Message);
    }
}
