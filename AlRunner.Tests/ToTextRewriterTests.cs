using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for the receiver.ToText(arg) rewriter rule (issue #1528).
///
/// BC lowers several AL ToText() built-in calls as instance calls with a session argument:
///   navValue.ToText(null!)          — BC runtime primitive
///   navValue.ToText(this.Session)   — BC runtime primitive, newer BC
///
/// The rewriter replaces these with AlCompat.Format(navValue) which returns string.
/// When the call site is already inside new NavText(…), the outer constructor
/// accepts the string — no CS0029 error.
///
/// Bug (issue #1528): user-defined AL procedures named ToText() are also intercepted.
/// BC scope classes call parent methods as _parent.ToText(this) — passing the scope
/// itself as the first argument. The rewriter was matching Count >= 1 and replacing
/// _parent.ToText(this) with AlCompat.Format(_parent) (string), while the
/// assignment target is NavText — causing CS0029.
///
/// Fix: exclude calls where the first argument is a bare `this` expression; those
/// are user-defined scope-to-parent calls, never BC runtime session arguments.
/// </summary>
public class ToTextRewriterTests
{
    private static string WrapInScope(string body) => $$"""
        namespace Microsoft.Dynamics.Nav.BusinessApplication
        {
            using AlRunner.Runtime;
            using Microsoft.Dynamics.Nav.Runtime;
            using Microsoft.Dynamics.Nav.Types;

            class Codeunit99991
            {
                public NavText ToText() { return NavText.Empty; }
            }

            class Scope_1 : AlScope
            {
                private Codeunit99991 _parent;
                public NavText result = NavText.Default(0);
                public NavText γretVal = NavText.Default(0);

                protected override void OnRun()
                {
                    {{body}}
                }
            }
        }
        """;

    // ─── Bug reproduction: _parent.ToText(this) ────────────────────────────

    /// <summary>
    /// Positive: _parent.ToText(this) (user-defined scope call with bare this) must
    /// NOT be rewritten to AlCompat.Format(_parent). The call returns NavText and
    /// assigning it to NavText result is valid — rewriting would produce CS0029.
    /// </summary>
    [Fact]
    public void UserToText_WithThisArg_IsNotRewritten()
    {
        var input = WrapInScope("this.result = _parent.ToText(this);");
        var output = RoslynRewriter.Rewrite(input);

        // Must NOT have been replaced with AlCompat.Format
        Assert.DoesNotContain("AlCompat.Format", output);
        // The original call must survive intact
        Assert.Contains("_parent.ToText(this)", output);
    }

    /// <summary>
    /// Positive: the preserved call still compiles by assigning NavText to NavText.
    /// (Compilation verified by the fix not introducing AlCompat.Format.)
    /// </summary>
    [Fact]
    public void UserToText_WithThisArg_CallPreserved_NotConvertedToString()
    {
        var input = WrapInScope("this.result = _parent.ToText(this);");
        var output = RoslynRewriter.Rewrite(input);

        // AlCompat.Format returns string — must not appear (would cause CS0029)
        Assert.DoesNotContain("AlCompat.Format(_parent)", output);
    }

    // ─── BC runtime cases: must still be rewritten ─────────────────────────

    /// <summary>
    /// Positive: navValue.ToText(null!) (BC runtime null session arg) MUST still
    /// be rewritten to AlCompat.Format(navValue). This is the original purpose of
    /// the rule and must not regress.
    /// </summary>
    [Fact]
    public void BcRuntimeToText_NullArg_IsRewritten()
    {
        var input = WrapInScope("this.γretVal = new NavText(someValue.ToText(null!));");
        var output = RoslynRewriter.Rewrite(input);

        // Must have been rewritten to AlCompat.Format
        Assert.Contains("AlCompat.Format", output);
        Assert.DoesNotContain(".ToText(null!)", output);
    }

    /// <summary>
    /// Positive: navValue.ToText(false) (Guid no-delimiter form) MUST still be
    /// rewritten to AlCompat.GuidToText(navValue, false). The false literal is NOT
    /// a bare `this`, so the fix must not block it.
    /// </summary>
    [Fact]
    public void BcRuntimeToText_FalseLiteralArg_IsRewrittenToGuidToText()
    {
        var input = WrapInScope("this.γretVal = new NavText(someGuid.ToText(false));");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("AlCompat.GuidToText", output);
        Assert.DoesNotContain(".ToText(false)", output);
    }

    // ─── Negative: 0-arg user ToText must also not be rewritten ───────────

    /// <summary>
    /// Negative: _parent.ToText() with 0 args (already guarded by Count >= 1)
    /// must not be rewritten. The fix must not break the existing guard.
    /// </summary>
    [Fact]
    public void UserToText_ZeroArgs_IsNotRewritten()
    {
        var input = WrapInScope("this.result = _parent.ToText();");
        var output = RoslynRewriter.Rewrite(input);

        Assert.DoesNotContain("AlCompat.Format", output);
        Assert.Contains("_parent.ToText()", output);
    }
}
