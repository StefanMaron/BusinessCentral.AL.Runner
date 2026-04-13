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
        if (!_enabled) return;
        _captures.Add((scopeName, objectName, variableName, value?.ToString(), statementId));
    }

    public static List<(string ScopeName, string ObjectName, string VariableName, string? Value, int StatementId)> GetCaptures()
        => new(_captures);

    public static void Reset()
    {
        _captures.Clear();
    }
}
