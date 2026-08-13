// TestPageBooleanValueTests — contract tests for AlRunner.TestPageBooleanValue (issue #1837,
// var-bound half — see MockTestPage.cs's TestPageBooleanValue doc comment for the full
// root-cause story).
//
// This is deliberately NOT a claim about what Business Central does (that claim is proven
// upstream — the three known-gaps-testpage.json entries removed/re-pointed in this same PR,
// backed by StefanMaron/BusinessCentral.AL.Language.Tests codeunit 60961 "Test Page Field
// Visible Tests" at the pinned submodule commit, which already runs against real BC). It is a
// claim about OUR OWN conversion helper: that it accepts exactly the spelling
// ITestField.ValueToString produces for a boolean ClientObject ("True"/"False",
// case-insensitively) and throws loudly — not silently defaulting — for anything else, so a
// SetValue(<Boolean>) round trip cannot silently start accepting arbitrary text.
//
// RED/GREEN: reverting TestPageBooleanValue.Resolve to `=> ALCompiler.ToNavValue(value)` (the
// pre-fix always-NavText fallback) makes the positive assertions here fail to compile (no such
// overload publicly testable) or, if inlined back into the caller instead, makes the companion
// corpus-backed known-gap entries reappear — this file exists so a regression in the helper
// itself is caught here, in milliseconds, without needing the BC engine loaded.
using AlRunner;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageBooleanValueTests
{
    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void Resolve_CanonicalSpelling_ReturnsMatchingNavBoolean(string input, bool expected)
    {
        var result = TestPageBooleanValue.Resolve(input, "unit test");

        var boolean = Assert.IsType<NavBoolean>(result);
        Assert.Equal(expected, boolean.Value);
    }

    // Negative: NOT the round-trip spelling this mock itself produces. Whether real BC's
    // TestPage.SetValue('Yes') on a Boolean control actually succeeds is a separate,
    // upstream-unvalidated question (see the TestPageBooleanValue doc comment) — this must
    // throw loudly rather than silently guessing, per .claude/rules/loud-failures.md.
    [Theory]
    [InlineData("Yes")]
    [InlineData("No")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("Blorp")]
    [InlineData("")]
    public void Resolve_AnyOtherSpelling_ThrowsOutOfScope_NamingTheReason(string input)
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(
            () => TestPageBooleanValue.Resolve(input, "unit test context"));

        Assert.Contains("testpage-boolean-value", ex.Reason);
        Assert.Contains("unit test context", ex.Message);
    }
}
