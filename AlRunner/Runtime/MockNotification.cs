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

    /// <summary>Send — dispatches to SendNotificationHandler if registered, otherwise no-op.</summary>
    public void ALSend(DataError errorLevel)
    {
        HandlerRegistry.InvokeSendNotificationHandler(this);
    }

    /// <summary>Send without DataError parameter.</summary>
    public void ALSend()
    {
        HandlerRegistry.InvokeSendNotificationHandler(this);
    }

    /// <summary>Recall — no-op in standalone mode. Returns true (BC semantics: true if recalled).</summary>
    public bool ALRecall(DataError errorLevel)
    {
        return true;
    }

    /// <summary>Recall without DataError parameter. Returns true.</summary>
    public bool ALRecall()
    {
        return true;
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

    /// <summary>
    /// Default — returns a new blank MockNotification.
    /// BC generates <c>MockNotification.Default</c> (property access, no args) for global
    /// Notification field initializers, e.g. in codeunit-level <c>var N: Notification</c>.
    /// Each access returns an independent new instance so mutations do not propagate between
    /// different variable declarations. Issue #1189.
    /// </summary>
    public static MockNotification Default => new MockNotification();

    /// <summary>
    /// ALAssign — copy all state from <paramref name="other"/> into this instance.
    /// BC emits <c>n.ALAssign(otherN)</c> for AL assignment <c>n := otherN</c>.
    /// </summary>
    public void ALAssign(MockNotification other)
    {
        if (other == null) return;
        ALId = other.ALId;
        ALMessage = other.ALMessage;
        ALScope = other.ALScope;
        _data.Clear();
        foreach (var kv in other._data)
            _data[kv.Key] = kv.Value;
        _actions.Clear();
        foreach (var a in other._actions)
            _actions.Add(a);
    }

    /// <summary>
    /// Clear — reset all state to defaults.
    /// BC emits <c>n.Clear()</c> / <c>Clear(n)</c> which the rewriter rewrites to <c>n.Clear()</c>.
    /// </summary>
    public void Clear()
    {
        ALId = Guid.NewGuid();
        ALMessage = string.Empty;
        ALScope = NotificationScope.LocalScope;
        _data.Clear();
        _actions.Clear();
    }
}
