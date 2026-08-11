// Ported from protocol-v2 (v1 architecture, PR #1609) as part of #1641.
namespace AlRunner;

/// <summary>
/// Execution-time signal about whether an exception was thrown inside a [Test] proc
/// (vs. setup / instantiation before any test proc ran). Drives Setup-vs-Runtime
/// classification.
/// </summary>
public record TestExecutionContext(bool InsideTestProc);

/// <summary>
/// Classifies a managed Exception into an <see cref="AlErrorKind"/> bucket so a
/// consumer (e.g. the VS Code extension) can vary the failure UI (assertion diff,
/// runtime stack, compile errors, etc.).
///
/// NOTE on the Assertion bucket in v2: v1 ran AL through a Roslyn-emitted C# pipeline
/// with its own <c>AlRunner.Runtime.AssertException</c> type, so a failed
/// <c>Assert.AreEqual</c> call was distinguishable from a plain <c>Error()</c> by
/// exception type. v2 runs unmodified MS/ISV DLLs in-process (see
/// .claude/rules/precompiled-dll-respect.md); AL's <c>Error()</c> — which is what
/// BC's real "Library Assert" / "Assert" test-library codeunits call internally —
/// always surfaces as the same <c>NavNCLDialogException</c> (verified empirically:
/// a bare <c>Error('boom')</c> in an AL test throws
/// <c>Microsoft.Dynamics.Nav.Types.NavNCLDialogException</c>, and BC's Assert
/// codeunits use that same call under the hood). There is therefore no distinct
/// runtime type to match on for the Assertion bucket today; those failures fall
/// through to Runtime. Distinguishing them would require inspecting the AL call
/// stack for a frame in a known assert-helper object, which is out of scope here —
/// see #1641's follow-up. The type-name heuristic below is kept (harmless,
/// forward-compatible) in case a future in-scope helper does throw a distinctly
/// named exception.
/// </summary>
public static class ErrorClassifier
{
    /// <summary>
    /// Classify the exception. A null exception maps to <see cref="AlErrorKind.Unknown"/>.
    /// </summary>
    public static AlErrorKind Classify(Exception? ex, TestExecutionContext ctx)
    {
        if (ex == null) return AlErrorKind.Unknown;

        // Assertion: match on suffix so we don't hard-couple to one runtime's exact type
        // identity. See the class-level note: no in-scope v2 type matches this today.
        var typeName = ex.GetType().Name;
        if (typeName.EndsWith("AssertException", StringComparison.Ordinal)
            || typeName.EndsWith("AssertionException", StringComparison.Ordinal))
        {
            return AlErrorKind.Assertion;
        }

        // Timeout: cancellation thrown by the cooperative timeout mechanism
        // (TestExecutor.InvokeWithTimeout).
        if (ex is OperationCanceledException) return AlErrorKind.Timeout;

        // Compile: the AL emit/compile pipeline wraps errors in a custom exception.
        // Match on type-name suffix (same rationale as Assertion).
        if (typeName.EndsWith("CompilationFailedException", StringComparison.Ordinal)
            || typeName.EndsWith("CompileErrorException", StringComparison.Ordinal))
        {
            return AlErrorKind.Compile;
        }

        // Anything thrown before we entered a [Test] proc (codeunit instantiation,
        // install-trigger seeding) is a setup/init failure.
        if (!ctx.InsideTestProc) return AlErrorKind.Setup;

        return AlErrorKind.Runtime;
    }

    /// <summary>
    /// Classify a completed <see cref="TestResult"/> for the protocol-v2
    /// <c>errorKind</c> field (#1641). Returns <c>null</c> when there is no error
    /// to classify — a pass or a skip. Emitting <c>unknown</c> in those cases would
    /// read on the wire as "this failed and we don't know why", so the field is
    /// omitted instead.
    ///
    /// This is the single place that knows how a v2 <see cref="TestResult"/> maps
    /// onto a bucket, so the CLI and the server can never drift apart:
    ///   <see cref="TestResult.TimedOut"/> is checked first because the timeout path
    ///   carries no exception at all (see the TestResult remarks) — without the flag
    ///   a hung test would come out as <see cref="AlErrorKind.Unknown"/>.
    ///   An error we raised ourselves with no exception behind it (e.g. "unsupported
    ///   test signature") also lands on Unknown, which is the honest answer.
    /// </summary>
    public static AlErrorKind? Classify(TestResult result)
    {
        if (result.Outcome is TestOutcome.Pass or TestOutcome.Skipped) return null;
        if (result.TimedOut) return AlErrorKind.Timeout;
        return Classify(result.Exception, new TestExecutionContext(result.InsideTestProc));
    }
}
