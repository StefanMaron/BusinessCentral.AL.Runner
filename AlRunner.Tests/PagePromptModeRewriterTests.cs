using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for issue #1266 — CS0120 on Page&lt;N&gt;.PromptMode static self-reference.
///
/// BC emits CurrPage.PromptMode inside a PromptDialog page as a static reference:
///   Page71116155.PromptMode  (the enclosing page class name, not an instance reference).
///
/// Because the rewriter strips the NavForm base class and injects PromptMode as an
/// instance property, the static access causes CS0120. The fix rewrites
///   Page&lt;N&gt;.PromptMode → this.PromptMode
/// when the access is inside the matching page class.
/// </summary>
public class PagePromptModeRewriterTests
{
    // Minimal BC-style C# that simulates a page class extending NavForm.
    // The rewriter strips NavForm, injects PromptMode as instance, and must
    // rewrite static self-references to instance references.
    private static string WrapInPageClass(string body, string className = "Page71116155") => $$"""
        namespace Microsoft.Dynamics.Nav.BusinessApplication
        {
            using AlRunner.Runtime;
            using Microsoft.Dynamics.Nav.Runtime;
            class {{className}} : NavForm {
                void OnInit() {
                    {{body}}
                }
            }
        }
        """;

    // ─── Positive: static PromptMode → this.PromptMode ───────────────────

    /// <summary>
    /// Positive: Page&lt;N&gt;.PromptMode (get) inside the page class is rewritten
    /// to this.PromptMode so it compiles as an instance access.
    /// </summary>
    [Fact]
    public void PromptMode_StaticGet_RewrittenToThis()
    {
        var input = WrapInPageClass("var pm = Page71116155.PromptMode;");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("this.PromptMode", output);
        Assert.DoesNotContain("Page71116155.PromptMode", output);
    }

    /// <summary>
    /// Positive: Page&lt;N&gt;.PromptMode (set) inside the page class is rewritten
    /// to this.PromptMode so the assignment compiles.
    /// </summary>
    [Fact]
    public void PromptMode_StaticSet_RewrittenToThis()
    {
        var input = WrapInPageClass("Page71116155.PromptMode = null;");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("this.PromptMode", output);
        Assert.DoesNotContain("Page71116155.PromptMode", output);
    }

    /// <summary>
    /// Positive: works with a different page ID — the rule is not hard-coded
    /// to a specific page number.
    /// </summary>
    [Fact]
    public void PromptMode_DifferentPageId_RewrittenToThis()
    {
        var input = WrapInPageClass(
            "var pm = Page99999.PromptMode;", "Page99999");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("this.PromptMode", output);
        Assert.DoesNotContain("Page99999.PromptMode", output);
    }

    // ─── Negative: non-matching patterns are NOT affected ─────────────────

    /// <summary>
    /// Negative: a reference to a DIFFERENT page class (not the enclosing one)
    /// is not rewritten. Only self-references are converted to this.
    /// </summary>
    [Fact]
    public void PromptMode_DifferentPageClass_NotRewritten()
    {
        // Inside Page71116155, referencing Page99999.PromptMode should NOT be rewritten.
        var input = WrapInPageClass("var pm = Page99999.PromptMode;");
        var output = RoslynRewriter.Rewrite(input);

        // The reference to Page99999 should remain (though PromptMode is not
        // defined on it, that's a separate compilation concern — the rewriter
        // must not touch cross-class references).
        Assert.DoesNotContain("this.PromptMode", output);
    }

    /// <summary>
    /// Negative: accessing a DIFFERENT member via PageNNN.X should not be
    /// affected by the PromptMode rule.
    /// </summary>
    [Fact]
    public void OtherMember_StaticAccess_NotRewrittenByPromptModeRule()
    {
        // Page71116155.SomeOtherProp should not be touched by the PromptMode rule.
        // (Other rewriter rules may handle it, but the PromptMode-specific rule must not.)
        var input = WrapInPageClass("var x = Page71116155.SomeOtherProp;");
        var output = RoslynRewriter.Rewrite(input);

        // The PromptMode rule should NOT have injected "this" for SomeOtherProp.
        // (The original text may be modified by other rewriter rules, but we check
        // that "this.SomeOtherProp" specifically does not appear.)
        Assert.DoesNotContain("this.SomeOtherProp", output);
    }

    /// <summary>
    /// Negative: in a non-page class (e.g. a codeunit), Page&lt;N&gt;.PromptMode
    /// is not rewritten because _currentPageClassName is not set.
    /// </summary>
    [Fact]
    public void PromptMode_InCodeunitClass_NotRewritten()
    {
        var input = """
            namespace Microsoft.Dynamics.Nav.BusinessApplication
            {
                using AlRunner.Runtime;
                using Microsoft.Dynamics.Nav.Runtime;
                class Codeunit50000 : NavCodeunit {
                    void M() {
                        var pm = Page71116155.PromptMode;
                    }
                }
            }
            """;
        var output = RoslynRewriter.Rewrite(input);

        Assert.DoesNotContain("this.PromptMode", output);
    }
}
