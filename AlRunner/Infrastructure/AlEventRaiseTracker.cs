using System.Reflection;

namespace AlRunner.Infrastructure;

/// <summary>
/// Per-test record of the AL events a test raised (#4988), for <c>affectedOnly</c> selection:
/// a changed event subscriber selects the tests that raised its event, which statement coverage
/// cannot see because a subscriber is reached through the event, not through a call.
///
/// Two observation points, both set inserts of a reference, and both active only while
/// <see cref="AlCoverageTracker.PerTestEnabled"/> is set and a test window is open:
/// <list type="bullet">
/// <item><see cref="NoteEventScope"/>, from <c>BcRuntime.DispatchCore</c>, for manually declared
/// events (<c>&lt;Event&gt;_Scope</c> classes). A publisher only reaches the dispatcher when its
/// scope's <c>γeventScope</c> is seeded, so recording seeds every scope of the request's bundle
/// modules (<c>EventSubscriberPatches.SeedAllEventScopesForRecording</c>).</item>
/// <item><see cref="NoteTriggerConsult"/>, prepended to BC's
/// <c>NCLMetaApplicationObject.IsEventSubscribed</c> and <c>NCLMetaField.IsEventSubscribed</c>,
/// which <c>NavRecord</c> insert/modify/delete/rename/validate consult for every table whether or
/// not anything subscribes. Recorded per object, not per trigger event.</item>
/// </list>
/// Keys: <c>ev|&lt;Kind&gt;|&lt;id&gt;|&lt;Event&gt;</c> and <c>trig|&lt;Kind&gt;|&lt;id&gt;</c>, the same
/// strings <c>EventSubscriberPatches.CurrentBundleSubscriberBindings</c> builds from a
/// subscriber's attribute. See docs/server-mode.md#affectedonly-and-event-subscribers.
/// </summary>
public static class AlEventRaiseTracker
{
    /// <summary>A trigger consult whose object could not be identified; matches every trig key.</summary>
    internal const string UnresolvedTriggerKey = "trig|?";

    /// <summary>
    /// The entry of <see cref="CollectPerTest"/> that belongs to no test (#5008): the table keys of
    /// records built outside any test window or held by a SingleInstance codeunit, which a later
    /// test can use without constructing one, plus an <c>ext|&lt;id&gt;</c> key for every
    /// tableextension whose base table was resolved. No test name contains '&lt;'.
    /// </summary>
    internal const string BundleWideKey = "<bundle>";

    private sealed class Bucket
    {
        public readonly HashSet<Type> Scopes = new();
        public readonly HashSet<object> MetaObjects = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<int> Tables = new();
    }

    private static readonly object _lock = new();
    private static readonly Dictionary<string, Bucket> _perTest = new(StringComparer.Ordinal);
    private static readonly HashSet<int> _longLivedTables = new();
    private static Bucket? _lastBucket;
    private static string? _lastKey;

    public static void ResetPerTest()
    {
        lock (_lock) { _perTest.Clear(); _longLivedTables.Clear(); _lastBucket = null; _lastKey = null; }
    }

    private static Bucket? CurrentBucket()
    {
        if (!AlCoverageTracker.PerTestEnabled) return null;
        var testKey = AlCoverageTracker.CurrentTestKey;
        if (testKey == null) return null;
        if (ReferenceEquals(testKey, _lastKey) && _lastBucket != null) return _lastBucket;
        if (!_perTest.TryGetValue(testKey, out var b)) _perTest[testKey] = b = new Bucket();
        _lastKey = testKey;
        _lastBucket = b;
        return b;
    }

    /// <summary>Called by the event dispatcher for every raised <c>&lt;Event&gt;_Scope</c>,
    /// before it looks for subscribers.</summary>
    public static void NoteEventScope(Type scopeType)
    {
        if (!AlCoverageTracker.PerTestEnabled) return;
        lock (_lock) CurrentBucket()?.Scopes.Add(scopeType);
    }

    /// <summary>Prepended to <c>IsEventSubscribed</c> on BC's table/page and field metadata:
    /// <paramref name="metaObject"/> is the <c>NCLMetaApplicationObject</c> or <c>NCLMetaField</c>.</summary>
    public static void NoteTriggerConsult(object? metaObject)
    {
        if (!AlCoverageTracker.PerTestEnabled || metaObject == null) return;
        lock (_lock) CurrentBucket()?.MetaObjects.Add(metaObject);
    }

    /// <summary>Prepended to BC's <c>NavRecord</c> constructor (#5008): a record of
    /// <paramref name="tableId"/> exists. Attributed to the open test, unless nothing is open or
    /// the record belongs to an instance that outlives the test (<see cref="OutlivesTheTest"/>).</summary>
    public static void NoteRecordConstructed(object? parent, int tableId)
    {
        if (!AlCoverageTracker.PerTestEnabled) return;
        var longLived = OutlivesTheTest(parent);
        lock (_lock)
        {
            var bucket = longLived ? null : CurrentBucket();
            if (bucket != null) bucket.Tables.Add(tableId);
            else _longLivedTables.Add(tableId);
        }
    }

    /// <summary>The keys each test raised since the last <see cref="ResetPerTest"/>, and the
    /// <see cref="BundleWideKey"/> entry.</summary>
    /// <param name="extensionsOfTable">Table id to the tableextension ids extending it now.</param>
    public static Dictionary<string, HashSet<string>> CollectPerTest(
        IReadOnlyDictionary<int, List<int>>? extensionsOfTable = null)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void AddTable(HashSet<string> keys, int id)
        {
            keys.Add(TableKey("Table", id));
            if (extensionsOfTable != null && extensionsOfTable.TryGetValue(id, out var exts))
                foreach (var e in exts) keys.Add(TableKey("TableExtension", e));
        }
        lock (_lock)
        {
            foreach (var (testKey, bucket) in _perTest)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scope in bucket.Scopes)
                    if (EventScopeKey(scope) is { } k) keys.Add(k);
                foreach (var meta in bucket.MetaObjects)
                    keys.Add(TriggerKeyOf(meta) ?? UnresolvedTriggerKey);
                foreach (var t in bucket.Tables) AddTable(keys, t);
                result[testKey] = keys;
            }
            var bundle = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in _longLivedTables) AddTable(bundle, t);
            if (extensionsOfTable != null)
                foreach (var exts in extensionsOfTable.Values)
                    foreach (var e in exts) bundle.Add(KnownExtensionKey(e));
            result[BundleWideKey] = bundle;
        }
        return result;
    }

    /// <summary>
    /// Walks the tree up from a new record's parent (its handle, which BC creates lazily on first
    /// use): a method scope first means a local, gone when the call returns; a test codeunit or a
    /// SingleInstance codeunit first means a global another test can read without building one.
    /// Anything unreadable counts as outliving the test — the direction that forces a full run.
    /// </summary>
    internal static bool OutlivesTheTest(object? node)
    {
        try
        {
            for (int depth = 0; node != null && depth < 64; depth++)
            {
                if (node is Microsoft.Dynamics.Nav.Runtime.NavTestCodeunit) return true;
                if (node is Microsoft.Dynamics.Nav.Runtime.NavCodeunit cu && cu.IsSingleInstance) return true;
                if (IsMethodScope(node.GetType())) return false;
                node = (node as Microsoft.Dynamics.Nav.Runtime.ITreeObject)?.Tree?.Parent;
            }
            return false; // reached the root (the session) through no scope and no long-lived codeunit
        }
        catch
        {
            return true;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _isMethodScope = new();

    private static bool IsMethodScope(Type type) => _isMethodScope.GetOrAdd(type, t =>
    {
        for (var b = t; b != null; b = b.BaseType)
            if (b.Name.StartsWith("NavMethodScope", StringComparison.Ordinal)) return true;
        return false;
    });

    /// <summary><c>tbl|Table|id</c>, or <c>tbl|TableExtension|id</c> for a table the extension
    /// extends: a test held a record of that table.</summary>
    internal static string TableKey(string kind, int id) => $"tbl|{kind}|{id}";

    /// <summary>In the <see cref="BundleWideKey"/> entry: the tableextension's base table was
    /// resolved when the baseline was recorded.</summary>
    internal static string KnownExtensionKey(int extensionId) => $"ext|{extensionId}";

    /// <summary><c>ev|Kind|id|Event</c> for a publisher's <c>&lt;Event&gt;_Scope</c> nested type, or
    /// null when its declaring type is not an AL object class.</summary>
    internal static string? EventScopeKey(Type scopeType)
    {
        var decl = scopeType.DeclaringType;
        if (decl == null) return null;
        if (!BcRuntime.TryDecodeEventPublisherDeclType(decl.Name, out var kind, out var id)) return null;
        var name = scopeType.Name;
        const string suffix = "_Scope";
        var cut = name.LastIndexOf(suffix, StringComparison.Ordinal);
        if (cut <= 0) return null;
        return EventKey(NormalizeDispatchKind(kind), id, name.Substring(0, cut));
    }

    internal static string EventKey(string kind, int id, string eventName) => $"ev|{kind}|{id}|{eventName}";

    internal static string TriggerKey(string kind, int id) => $"trig|{kind}|{id}";

    /// <summary>The dispatcher's declaring-type prefix to the object kind a subscriber names:
    /// a table's own code compiles to <c>Record&lt;N&gt;</c>.</summary>
    internal static string NormalizeDispatchKind(string dispatchKind)
        => dispatchKind == BcRuntime.PublisherKindTable ? "Table" : dispatchKind;

    /// <summary>BC's <c>ObjectType</c> ordinal (Table=1, Report=3, CodeUnit=5, XmlPort=6, Page=8,
    /// Query=9) to the kind used in keys; null for any other.</summary>
    internal static string? KindOfObjectType(int objectTypeOrdinal) => objectTypeOrdinal switch
    {
        1 => "Table",
        3 => "Report",
        5 => "Codeunit",
        6 => "XmlPort",
        8 => "Page",
        9 => "Query",
        _ => null,
    };

    private static string? TriggerKeyOf(object meta)
    {
        try
        {
            var owner = meta.GetType().Name == "NCLMetaField"
                ? meta.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(meta)
                : meta;
            if (owner == null) return null;
            var aoid = owner.GetType().GetProperty("ApplicationObjectId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(owner);
            if (aoid == null) return null;
            var t = aoid.GetType();
            var ot = t.GetProperty("ObjectType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(aoid);
            var on = t.GetProperty("ObjectNumber", BindingFlags.Public | BindingFlags.Instance)?.GetValue(aoid);
            if (ot == null || on is not int id) return null;
            var kind = KindOfObjectType(Convert.ToInt32(ot)) ?? ot.ToString();
            return TriggerKey(kind!, id);
        }
        catch
        {
            return null;
        }
    }
}
