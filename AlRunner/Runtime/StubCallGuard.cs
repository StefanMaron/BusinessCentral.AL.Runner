namespace AlRunner.Runtime;

/// <summary>
/// Thrown when --fail-on-stub is active and a blank shell stub or documented
/// runtime no-op is called. Allows callers to catch specifically.
/// </summary>
public class RunnerGapException : Exception
{
    public RunnerGapException(string message) : base(message) { }
}

/// <summary>
/// Guards against accidental test passes caused by blank shell stubs or runtime no-ops.
/// When <see cref="FailOnStub"/> is true (set by --fail-on-stub), any call to a
/// generated blank shell or a documented no-op throws <see cref="RunnerGapException"/>.
/// </summary>
public static class StubCallGuard
{
    /// <summary>
    /// When true, blank-shell calls and runtime no-ops throw rather than silently succeeding.
    /// Set to <c>options.FailOnStub</c> at the start of each pipeline run.
    /// Thread-static so concurrent tests in different threads don't interfere.
    /// </summary>
    [ThreadStatic]
    public static bool FailOnStub;

    /// <summary>
    /// Maps auto-stubbed codeunit IDs to their display name (e.g. "Sales Header").
    /// Populated by the pipeline when auto-stubs are generated.
    /// Used by <see cref="MockCodeunitHandle"/> to report the correct name in the error message.
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string>
        AutoStubbedCodeunitNames = new();

    /// <summary>
    /// Check whether the given object/procedure is a blank shell auto-stub.
    /// Call this at the top of every generated auto-stub body.
    /// </summary>
    public static void CheckStub(string objectName, string procedureName)
    {
        if (!FailOnStub) return;
        throw new RunnerGapException(
            $"'{objectName}.{procedureName}' is a blank shell — " +
            $"add this object to your compiled dependency slice or remove --fail-on-stub.");
    }

    /// <summary>
    /// Check whether the codeunit with the given ID is an auto-stubbed blank shell.
    /// Called by <see cref="MockCodeunitHandle"/> before dispatching to a stub codeunit.
    /// </summary>
    /// <param name="codeunitId">The codeunit ID being invoked.</param>
    /// <param name="procedureName">The procedure name being called (for the error message).</param>
    public static void CheckStubById(int codeunitId, string? procedureName = null)
    {
        if (!FailOnStub) return;
        if (!AutoStubbedCodeunitNames.TryGetValue(codeunitId, out var name))
            return;
        var proc = procedureName != null ? $".{procedureName}" : "";
        throw new RunnerGapException(
            $"'{name}{proc}' (codeunit {codeunitId}) is a blank shell — " +
            $"add this object to your compiled dependency slice or remove --fail-on-stub.");
    }

    /// <summary>
    /// Check whether the named operation is a documented runtime no-op.
    /// Call this at the top of any runtime method that does nothing but would have
    /// real side-effects in live BC.
    /// </summary>
    public static void CheckNoOp(string description)
    {
        if (!FailOnStub) return;
        throw new RunnerGapException(
            $"'{description}' is a no-op in the runner — " +
            $"this call has no effect here but does in real BC. Remove --fail-on-stub or adjust the test.");
    }
}
