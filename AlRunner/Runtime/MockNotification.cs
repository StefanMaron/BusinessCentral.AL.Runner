namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory replacement for NavNotification.
/// NavNotification.ALSend/ALRecall/ALAddAction require NavSession and the BC service
/// tier. This mock stores all state locally and makes Send/Recall/AddAction no-ops.
/// SetData/GetData/HasData use an in-memory dictionary.
/// </summary>
public class MockNotification
{
    private readonly Dictionary<string, string> _data = new();
    private readonly List<(string Caption, int CodeunitId, string MethodName)> _actions = new();

    /// <summary>Auto-generated Id, assigned on construction (matches BC behavior).</summary>
    public Guid ALId { get; set; } = Guid.NewGuid();

    /// <summary>Notification message text.</summary>
    public string ALMessage { get; set; } = string.Empty;

    /// <summary>Notification scope (LocalScope or GlobalScope). Stored but not enforced.</summary>
    public NotificationScope ALScope { get; set; } = NotificationScope.LocalScope;

    /// <summary>Send — no-op in standalone mode (no UI to display to).</summary>
    public void ALSend(DataError errorLevel)
    {
        // No-op: standalone mode has no notification UI.
    }

    /// <summary>Send without DataError parameter.</summary>
    public void ALSend()
    {
        // No-op
    }

    /// <summary>Recall — no-op in standalone mode.</summary>
    public void ALRecall(DataError errorLevel)
    {
        // No-op
    }

    /// <summary>Recall without DataError parameter.</summary>
    public void ALRecall()
    {
        // No-op
    }

    /// <summary>Store a key-value pair on the notification.</summary>
    public void ALSetData(string key, string value)
    {
        _data[key] = value;
    }

    /// <summary>Retrieve data by key. Returns empty string if not found.</summary>
    public string ALGetData(string key)
    {
        return _data.TryGetValue(key, out var value) ? value : string.Empty;
    }

    /// <summary>Check whether a key has been set.</summary>
    public bool ALHasData(string key)
    {
        return _data.ContainsKey(key);
    }

    /// <summary>Register an action on the notification. Stored but not invoked in standalone mode.</summary>
    public void ALAddAction(string actionCaption, int codeunitId, string functionName)
    {
        _actions.Add((actionCaption, codeunitId, functionName));
    }

    /// <summary>AddAction with description parameter.</summary>
    public void ALAddAction(string actionCaption, int codeunitId, string functionName, string description)
    {
        _actions.Add((actionCaption, codeunitId, functionName));
    }
}
