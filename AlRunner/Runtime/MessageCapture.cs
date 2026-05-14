namespace AlRunner.Runtime;

public static class MessageCapture
{
    private static readonly List<string> _messages = new();
    private static bool _enabled;

    public static void Enable() => _enabled = true;
    public static void Disable() => _enabled = false;
    public static void Reset() => _messages.Clear();

    public static void Capture(string message)
    {
        // Per-test scope gets the message for isolation (no _enabled guard — the scope
        // is itself the opt-in mechanism so per-test isolation always works).
        var scope = TestExecutionScope.Current;
        if (scope != null)
            scope.Messages.Add(message);

        // Global aggregate also gets the message when capture mode is enabled,
        // so the pipeline-level MessageCapture.GetMessages() remains populated.
        if (_enabled)
            _messages.Add(message);
    }

    public static List<string> GetMessages() => new(_messages);
}
