namespace AlRunner.Runtime;

/// <summary>
/// Static collector for variable value captures during test execution.
/// When capture mode is enabled, generated code calls Capture() at each statement
/// to record variable values for Quokka-style inline display.
/// </summary>
public static class ValueCapture
{
    private static readonly List<(string ScopeName, string ObjectName, string VariableName, string? Value, int StatementId)> _captures = new();
    private static bool _enabled;

    public static bool Enabled => _enabled;

    public static void Enable() => _enabled = true;
    public static void Disable() => _enabled = false;

    public static void Capture(string scopeName, string objectName, string variableName, object? value, int statementId)
    {
        var stringValue = value?.ToString();
        var entry = (scopeName, objectName, variableName, stringValue, statementId);

        // Per-test scope gets the capture for isolation.
        var scope = TestExecutionScope.Current;
        if (scope != null)
            scope.CapturedValues.Add(entry);

        // Plan E5 Group A: route to the innermost active loop's per-iteration
        // accumulator. RecordCapture is a no-op when no loop is active or when
        // IterationTracker is disabled, so this is safe to call unconditionally.
        IterationTracker.RecordCapture(variableName, stringValue);

        // Global aggregate also gets the capture when capture mode is enabled,
        // so the pipeline-level ValueCapture.GetCaptures() remains populated.
        if (_enabled)
            _captures.Add(entry);
    }

    public static List<(string ScopeName, string ObjectName, string VariableName, string? Value, int StatementId)> GetCaptures()
        => new(_captures);

    public static void Reset()
    {
        _captures.Clear();
    }
}
