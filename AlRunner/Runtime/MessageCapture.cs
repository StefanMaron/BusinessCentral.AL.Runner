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
        if (!_enabled) return;
        _messages.Add(message);
    }

    public static List<string> GetMessages() => new(_messages);
}
