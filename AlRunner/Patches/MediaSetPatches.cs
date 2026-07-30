// MediaSetPatches — in-memory backing for NavMediaSet AL methods (PAGE-REPORT-CLUSTERS §4).
//
// BC's NavMediaSet.ALInsert / ALRemove / ALItem / get_ALCount all reach the database /
// Session tier which is not present in the runner.
//
// Root cause: NavRecord.GetFieldValueSafe creates a fresh NavMediaSet copy via
// `new NavMediaSet(other)` every time AL accesses a MediaSet field. We cannot key on
// the NavMediaSet instance (different each time) or the NavGuid Key (all Guid.Empty
// records share the same Key). Instead we key on (ParentRecord, FieldNo) — the parent
// NavRecord reference (same object for the same AL record variable) and the field index.
// Both are available from NavMediaValueBase.ParentRecord / .FieldNo after
// SetOwnerRecordInformation is called by UpdateMediaFieldInformation.
//
// ALImport (ImportFile) returns a fresh Guid; ALExport (ExportFile) returns 0.
//
// Hook installation: BcRuntime.cs ApplyNavMediaSetPatches block.
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

public static class MediaSetPatches
{
    // Backing store: keyed on (ParentRecord, FieldNo) tuple. All NavMediaSet copies
    // for the same record field share the same ParentRecord instance and FieldNo.
    private static readonly ConditionalWeakTable<object, Dictionary<int, List<Guid>>> _recStore = new();

    // Lazy-initialized reflectors for NavMediaValueBase.ParentRecord and .FieldNo.
    private static PropertyInfo? _parentRecordProp;
    private static PropertyInfo? _fieldNoProp;

    private static (object? parentRec, int fieldNo) GetRecordKey(object self)
    {
        if (_parentRecordProp == null)
        {
            var t = self.GetType();
            _parentRecordProp = t.GetProperty("ParentRecord",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                ?? t.BaseType?.GetProperty("ParentRecord",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            _fieldNoProp = t.GetProperty("FieldNo",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
                ?? t.BaseType?.GetProperty("FieldNo",
                    BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        }
        var parentRec = _parentRecordProp?.GetValue(self);
        var fieldNo = _fieldNoProp?.GetValue(self) is int fn ? fn : 0;
        return (parentRec, fieldNo);
    }

    private static List<Guid> GetList(object self)
    {
        var (parentRec, fieldNo) = GetRecordKey(self);
        // If we have no parent record, fall back to self as key.
        var storeKey = parentRec ?? self;
        var dict = _recStore.GetValue(storeKey, _ => new Dictionary<int, List<Guid>>());
        lock (dict)
        {
            if (!dict.TryGetValue(fieldNo, out var list))
                dict[fieldNo] = list = new List<Guid>();
            return list;
        }
    }

    // ── ALInsert(DataError errorLevel, Guid mediaId) → bool ─────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavMediaSet_ALInsert(object self, object errorLevel, Guid mediaId)
    {
        var (parentRec, fieldNo) = GetRecordKey(self);
        Console.Error.WriteLine($"[NavMediaSet.ALInsert] hooked → adding {mediaId} (rec={RuntimeHelpers.GetHashCode(parentRec ?? self)}, field={fieldNo})");
        var list = GetList(self);
        lock (list)
        {
            if (!list.Contains(mediaId))
                list.Add(mediaId);
        }
        return true;
    }

    // ── ALRemove(DataError errorLevel, Guid mediaId) → bool ─────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavMediaSet_ALRemove(object self, object errorLevel, Guid mediaId)
    {
        Console.Error.WriteLine($"[NavMediaSet.ALRemove] hooked → removing {mediaId}");
        var list = GetList(self);
        lock (list)
            return list.Remove(mediaId);
    }

    // ── get_ALCount() → int ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavMediaSet_get_ALCount(object self)
    {
        Console.Error.WriteLine($"[NavMediaSet.get_ALCount] hooked");
        var list = GetList(self);
        lock (list)
            return list.Count;
    }

    // ── ALItem(int index) → Guid  (1-based, per BC AL convention) ───────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_ALItem(object self, int index)
    {
        Console.Error.WriteLine($"[NavMediaSet.ALItem] hooked → index {index}");
        var list = GetList(self);
        lock (list)
            return (index >= 1 && index <= list.Count) ? list[index - 1] : Guid.Empty;
    }

    // ── ALImport(DataError, string fileName, string description) → Guid ──────────────────
    // Covers the ImportFile(fileName, description) AL overload.

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_ALImport_File2(object self, object errorLevel, string fileName, string description)
    {
        Console.Error.WriteLine($"[NavMediaSet.ALImport/file2] hooked → fileName={fileName}");
        var id = Guid.NewGuid();
        var list = GetList(self);
        lock (list)
            list.Add(id);
        return id;
    }

    // ── ALImport(DataError, string fileName, string description, string mimeType) → Guid ─

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_ALImport_File3(object self, object errorLevel, string fileName, string description, string mimeType)
    {
        Console.Error.WriteLine($"[NavMediaSet.ALImport/file3] hooked → fileName={fileName}");
        var id = Guid.NewGuid();
        var list = GetList(self);
        lock (list)
            list.Add(id);
        return id;
    }

    // ── ALExport(DataError, string fileBaseName) → int ───────────────────────────────────
    // Returns 0 (no blob data in standalone mode).

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavMediaSet_ALExport(object self, object errorLevel, string fileBaseName)
    {
        Console.Error.WriteLine($"[NavMediaSet.ALExport] hooked → returning 0 (no data)");
        return 0;
    }

    // ── get_ALMediaId() → Guid  (MediaSet container identity) ───────────────────────────
    // Declared on NavMediaValueBase.  Returns a stable non-empty Guid per (ParentRecord,
    // FieldNo) key — generated once and cached so repeated calls return the same value.

    private static readonly ConditionalWeakTable<object, Dictionary<int, Guid>> _mediaIds = new();

    public static Guid GetOrCreateMediaId(object self)
    {
        var (parentRec, fieldNo) = GetRecordKey(self);
        var storeKey = parentRec ?? self;
        var dict = _mediaIds.GetValue(storeKey, _ => new Dictionary<int, Guid>());
        lock (dict)
        {
            if (!dict.TryGetValue(fieldNo, out var id))
                dict[fieldNo] = id = Guid.NewGuid();
            return id;
        }
    }

    // TEMPORARY (memory-census diagnostic) — total Guid entries stored across all
    // (ParentRecord, FieldNo) keys. ConditionalWeakTable has no direct Count, so this
    // sums each tracked entry's inner dictionary list lengths. See MemoryCensus.cs.
    internal static int CensusEntryCount()
    {
        int n = 0;
        foreach (var (_, dict) in _recStore)
            foreach (var (_, list) in dict)
                n += list.Count;
        return n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_get_ALMediaId(object self)
    {
        var id = GetOrCreateMediaId(self);
        Console.Error.WriteLine($"[NavMediaSet.get_ALMediaId] hooked → {id}");
        return id;
    }
}
