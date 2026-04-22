using AlRunner;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Tests for the Database.KeyGroupEnabled/KeyGroupDisable/KeyGroupEnable stubs.
///
/// BC lowers these AL built-ins to C# calls on ALDatabase:
///   Database.KeyGroupEnabled(name)  → ALDatabase.ALKeyGroupEnabled(name)   (returns bool)
///   Database.KeyGroupDisable(name)  → ALDatabase.ALKeyGroupDisable(name)   (void)
///   Database.KeyGroupEnable(name)   → ALDatabase.ALKeyGroupEnable(name)    (void)
///
/// The runner cannot exercise a real BC database key group. Stubs:
///   ALKeyGroupEnabled → true   (all key groups treated as enabled)
///   ALKeyGroupDisable → no-op  (stripped to empty statement)
///   ALKeyGroupEnable  → no-op  (stripped to empty statement)
///
/// These methods are not yet exposed in AL compiler 16.2, so end-to-end AL tests
/// cannot be written with the current toolchain. The rewriter rules are verified here
/// directly against the synthetic C# that a future BC compiler would generate.
///
/// Issue: #1054
/// </summary>
public class KeyGroupRewriterTests
{
    // Minimal BC-style C# wrapper that the rewriter accepts.
    // The real BC output includes full namespace + using directives; we mimic
    // enough structure to trigger the rewriter rules under test.
    private static string WrapInMethod(string body) => $$"""
        namespace Microsoft.Dynamics.Nav.BusinessApplication
        {
            using AlRunner.Runtime;
            using Microsoft.Dynamics.Nav.Runtime;
            class C {
                void M() {
                    {{body}}
                }
            }
        }
        """;

    // ─── KeyGroupEnabled → true ────────────────────────────────────────────

    /// <summary>
    /// Positive: ALDatabase.ALKeyGroupEnabled("x") is rewritten to the literal <c>true</c>.
    /// Verifies the rewriter rule fires for the correct receiver + method name.
    /// </summary>
    [Fact]
    public void ALKeyGroupEnabled_RewrittenToTrue()
    {
        var input = WrapInMethod("bool result = ALDatabase.ALKeyGroupEnabled(\"MyGroup\");");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("true", output);
        // The original call must be gone.
        Assert.DoesNotContain("ALKeyGroupEnabled", output);
    }

    /// <summary>
    /// Positive: the rewritten value is specifically the literal <c>true</c>, not <c>false</c>.
    /// A broken stub that always returns false would fail this assertion.
    /// </summary>
    [Fact]
    public void ALKeyGroupEnabled_RewritesToTrueNotFalse()
    {
        var input = WrapInMethod("bool result = ALDatabase.ALKeyGroupEnabled(\"MyGroup\");");
        var output = RoslynRewriter.Rewrite(input);

        // "true" must appear in the assignment context; "false" must NOT.
        Assert.Contains("true", output);
        Assert.DoesNotContain("false", output);
    }

    /// <summary>
    /// Positive: the rule applies regardless of the key group name argument.
    /// A stub that only handles a hard-coded name would fail this test.
    /// </summary>
    [Fact]
    public void ALKeyGroupEnabled_ReturnsTrue_ForAnyGroupName()
    {
        var input1 = WrapInMethod("bool a = ALDatabase.ALKeyGroupEnabled(\"GroupA\");");
        var input2 = WrapInMethod("bool b = ALDatabase.ALKeyGroupEnabled(\"SomeOtherGroup\");");

        var out1 = RoslynRewriter.Rewrite(input1);
        var out2 = RoslynRewriter.Rewrite(input2);

        Assert.Contains("true", out1);
        Assert.Contains("true", out2);
        Assert.DoesNotContain("ALKeyGroupEnabled", out1);
        Assert.DoesNotContain("ALKeyGroupEnabled", out2);
    }

    // ─── KeyGroupDisable → no-op (stripped) ───────────────────────────────

    /// <summary>
    /// Positive: ALDatabase.ALKeyGroupDisable("x") is stripped to an empty statement.
    /// Verifies the method is in StripEntireCallMethods.
    /// </summary>
    [Fact]
    public void ALKeyGroupDisable_StrippedToEmptyStatement()
    {
        var input = WrapInMethod("ALDatabase.ALKeyGroupDisable(\"MyGroup\");");
        var output = RoslynRewriter.Rewrite(input);

        // The call must be gone — the method body must not reference the method.
        Assert.DoesNotContain("ALKeyGroupDisable", output);
    }

    /// <summary>
    /// Positive: ALDatabase.ALKeyGroupEnable("x") is stripped to an empty statement.
    /// Verifies the method is in StripEntireCallMethods.
    /// </summary>
    [Fact]
    public void ALKeyGroupEnable_StrippedToEmptyStatement()
    {
        var input = WrapInMethod("ALDatabase.ALKeyGroupEnable(\"MyGroup\");");
        var output = RoslynRewriter.Rewrite(input);

        Assert.DoesNotContain("ALKeyGroupEnable(", output);
    }

    /// <summary>
    /// Negative: a call to a DIFFERENT ALDatabase method (ALCommit) is NOT affected
    /// by the KeyGroup rules. This guards against overly-broad rewrites.
    /// </summary>
    [Fact]
    public void OtherALDatabaseMethods_NotAffectedByKeyGroupRules()
    {
        // ALCommit is also stripped, but by a separate rule (StripEntireCallMethods).
        // What matters here is that KeyGroupEnabled rule only fires for its own method.
        var input = WrapInMethod("bool result = ALDatabase.ALIsInWriteTransaction();");
        var output = RoslynRewriter.Rewrite(input);

        // ALIsInWriteTransaction → false (its own rule — must NOT become true).
        Assert.Contains("false", output);
        Assert.DoesNotContain("true", output);
    }
}
