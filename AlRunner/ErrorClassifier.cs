namespace AlRunner;

/// <summary>
/// Execution-time signals about whether an exception was thrown inside a [Test] proc
/// (vs. setup / [OnRun] before any test ran). Drives Setup-vs-Runtime classification.
/// </summary>
/// <remarks>
/// Single-bool today; T8 will expand to carry per-test messages and capturedValues via AsyncLocal.
/// </remarks>
public record TestExecutionContext(bool InsideTestProc);

/// <summary>
/// Classifies a managed Exception into an <see cref="AlErrorKind"/> bucket so the
/// IDE can vary the failure UI (assertion diff, runtime stack, compile errors, etc.).
/// </summary>
public static class ErrorClassifier
{
    /// <summary>
    /// Classify the exception. A null exception maps to <see cref="AlErrorKind.Unknown"/>.
    /// </summary>
    public static AlErrorKind Classify(Exception? ex, TestExecutionContext ctx)
    {
        if (ex == null) return AlErrorKind.Unknown;

        // Assertion: match on suffix so we don't hard-couple to the runtime's exact type
        // identity. AlRunner.Runtime.AssertException ends with "AssertException";
        // third-party runners may use "AssertionException".
        var typeName = ex.GetType().Name;
        if (typeName.EndsWith("AssertException", StringComparison.Ordinal)
            || typeName.EndsWith("AssertionException", StringComparison.Ordinal))
        {
            return AlErrorKind.Assertion;
        }

        // Timeout: cancellation thrown by the cooperative timeout mechanism.
        if (ex is OperationCanceledException) return AlErrorKind.Timeout;

        // Compile: Roslyn pipeline wraps emit/compile errors in a custom exception.
        // Match on type-name suffix (same rationale as Assertion).
        if (typeName.EndsWith("CompilationFailedException", StringComparison.Ordinal)
            || typeName.EndsWith("CompileErrorException", StringComparison.Ordinal))
        {
            return AlErrorKind.Compile;
        }

        // Anything thrown before we entered a [Test] proc is a setup/init failure.
        if (!ctx.InsideTestProc) return AlErrorKind.Setup;

        return AlErrorKind.Runtime;
    }
}
