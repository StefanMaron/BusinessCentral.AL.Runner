// TestPageDateValueTests — contract tests for AlRunner.TestPageDateValue (issue #2054, the
// page-variable-Date half — see MockTestPage.cs's TestPageDateValue doc comment for the full
// root-cause story).
//
// This is deliberately NOT a claim about what Business Central does (that claim belongs
// upstream, in StefanMaron/BusinessCentral.AL.Language.Tests, where a real BC service tier can
// adjudicate it). It is a claim about OUR OWN conversion helper: that it accepts exactly the
// spelling PageVariableTestField.ValueToString produces for a Date ClientObject (.NET's
// InvariantCulture general date/time pattern) and throws loudly — not silently defaulting —
// for anything else, so a SetValue(Date) round trip cannot silently start accepting arbitrary
// text or silently coerce an unparseable string into a wrong date.
//
// RED/GREEN: before this fix, PageVariableTestField.ToBoundValue had no case for NavDate at
// all and fell through to ALCompiler.ToNavValue(value) — a NavText — which the page's own
// generated setter for a Date global then rejected with
// "Unable to cast object of type 'NavText' to type 'NavDate'". Reverting
// TestPageDateValue.Resolve to that same always-NavText fallback makes the positive assertion
// here fail (no NavDate is ever produced), which is exactly the regression this file exists to
// catch in milliseconds, without needing the BC engine loaded.
using System;
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageDateValueTests
{
    // Positive: the exact spelling .NET's Convert.ToString(DateTime, InvariantCulture) produces
    // — the general date/time pattern "MM/dd/yyyy HH:mm:ss" — round-trips to the same date.
    [Fact]
    public void Resolve_InvariantCultureGeneralFormat_ReturnsMatchingNavDate()
    {
        var result = TestPageDateValue.Resolve("12/31/2026 00:00:00", "unit test");

        var date = Assert.IsType<NavDate>(result);
        Assert.Equal(new DateTime(2026, 12, 31), date.Value.Date);
    }

    [Fact]
    public void Resolve_DifferentDate_ReturnsThatExactDate()
    {
        var result = TestPageDateValue.Resolve("01/15/2020 00:00:00", "unit test");

        var date = Assert.IsType<NavDate>(result);
        Assert.Equal(new DateTime(2020, 1, 15), date.Value.Date);
    }

    // Negative: NOT the round-trip spelling this mock itself produces. "31/12/2026" is a
    // day-first spelling InvariantCulture's month-first parser rejects (month 31 is invalid) —
    // whether real BC's own TestPage.SetValue accepts other date spellings is a separate,
    // upstream-unvalidated question; this must throw loudly rather than silently guessing, per
    // .claude/rules/loud-failures.md.
    [Theory]
    [InlineData("31/12/2026")]
    [InlineData("not a date")]
    [InlineData("")]
    public void Resolve_AnythingElse_ThrowsOutOfScope_NamingTheReason(string input)
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => TestPageDateValue.Resolve(input, "unit test context"));

        Assert.Contains("testpage-date-value", ex.Reason);
        Assert.Contains("unit test context", ex.Message);
    }
}
