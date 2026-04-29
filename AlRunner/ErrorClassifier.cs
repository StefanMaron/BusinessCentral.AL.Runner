namespace AlRunner;

/// <summary>
/// Execution-time signals about whether an exception was thrown inside a [Test] proc
/// (vs. setup / [OnRun] before any test ran). Drives Setup-vs-Runtime classification.
/// </summary>
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
    public static AlErrorKind Classify(Exception ex, TestExecutionContext ctx)
    {
        if (ex == null) return AlErrorKind.Unknown;

        // Assertion: AlRunner.Runtime.MockAssert throws subclasses with "AssertException"
        // or "AssertionException" in the type name. Match on suffix to avoid hard
        // coupling to the runtime's exact type identity.
        var typeName = ex.GetType().Name;
        if (typeName.Contains("AssertException", StringComparison.Ordinal)
            || typeName.Contains("AssertionException", StringComparison.Ordinal)
            || typeName.Contains("MockAssert", StringComparison.Ordinal))
        {
            return AlErrorKind.Assertion;
        }

        // Timeout: cancellation thrown by the cooperative timeout mechanism.
        if (ex is OperationCanceledException) return AlErrorKind.Timeout;

        // Compile: Roslyn pipeline wraps emit/compile errors in a custom exception.
        // Match on type-name suffix (same rationale as Assertion).
        if (typeName.Contains("CompilationFailed", StringComparison.Ordinal)
            || typeName.Contains("CompileError", StringComparison.Ordinal))
        {
            return AlErrorKind.Compile;
        }

        // Anything thrown before we entered a [Test] proc is a setup/init failure.
        if (!ctx.InsideTestProc) return AlErrorKind.Setup;

        return AlErrorKind.Runtime;
    }
}
