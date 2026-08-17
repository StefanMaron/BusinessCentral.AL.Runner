// RecordLinkPatches — in-scope faithful replacement for BC's RecordLink (table 2000000068).
//
// Real BC stores RecordLink rows in the platform RecordLink table and reads/writes
// them through DataAccessSource.TenantDataAccess. Our skeleton doesn't wire a
// TenantDataAccess for system tables, so the unmodified BC bodies NRE inside the
// NavRecord 7-arg ctor invoked by `new NavRecord(record, 2000000068)`.
//
// Per docs/scope.md §2 (faithful replacement) we replace the AL-facing RecordLink
// surface on NavRecord with an in-memory store. AL-observable contract is preserved:
//   - AddLink returns a positive, monotonically-increasing link id (BC's CreateNew
//     emits monotone ids inside a session — sequence semantics are equivalent).
//   - HasLinks returns true iff at least one link is attached to this record.
//   - DeleteLinks removes every link for this record. DeleteLink removes the one.
//   - CopyLinks copies every link from `fromRecord` onto this record (new ids).
//
// Keying: parent record's ALRecordId (TableId + primary-key values). Same record
// at different points in a test still hashes to the same key.
//
// Why hook NavRecord.AL*Link* and not RecordLink.*Async: NavRecord.AL*Link methods
// are the entry points AL-emitted output calls (sync wrappers around Async). JmpHook
// on those reaches AL output (JIT'd code outside Ncl.dll R2R) — the
// `precompiled-dll-respect.md` R2R-internal-call caveat doesn't bite for AL callers.

using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

public static class RecordLinkPatches
{
    private sealed record Entry(int Id, string Url, string Description);

    private static readonly Dictionary<NavRecordId, List<Entry>> _links =
        new(EqualityComparer<NavRecordId>.Default);
    private static int _nextId = 0;
    private static readonly object _lock = new();

    /// <summary>
    /// Drop every stored link. Called from RecordPatches.ResetPerTestState before each
    /// test runs so links don't leak across tests (BC's real semantics: rollback at
    /// end-of-test wipes the link table state).
    /// </summary>
    public static void ResetForTest()
    {
        lock (_lock)
        {
            _links.Clear();
            _nextId = 0;
        }
    }

    internal static object CaptureInstallBaseline()
    {
        lock (_lock)
            return (_links.ToDictionary(pair => pair.Key, pair => pair.Value.ToList()), _nextId);
    }

    internal static void RestoreInstallBaseline(object? snapshot)
    {
        lock (_lock)
        {
            _links.Clear();
            _nextId = 0;
            if (snapshot is not ValueTuple<Dictionary<NavRecordId, List<Entry>>, int> state) return;
            foreach (var pair in state.Item1)
                _links[pair.Key] = pair.Value.ToList();
            _nextId = state.Item2;
        }
    }

    /// <summary>Write the record-link half of an install baseline to the on-disk baseline
    /// cache (see RecordPatches.InstallBaselineDisk). Lives here because <see cref="Entry"/>
    /// and the id counter are private to this store. NavRecordId keys go through BC's own
    /// GetBytes/CreateFromBytes pair, the same encoding the service tier uses for a RecordId
    /// field value.</summary>
    internal static void SerializeInstallBaseline(BinaryWriter w, object? snapshot)
    {
        if (snapshot is not ValueTuple<Dictionary<NavRecordId, List<Entry>>, int> state)
        {
            w.Write(-1);
            return;
        }
        w.Write(state.Item1.Count);
        foreach (var (recordId, entries) in state.Item1)
        {
            var idBytes = recordId.GetBytes();
            w.Write(idBytes.Length);
            w.Write(idBytes);
            w.Write(entries.Count);
            foreach (var e in entries)
            {
                w.Write(e.Id);
                w.Write(e.Url);
                w.Write(e.Description);
            }
        }
        w.Write(state.Item2);
    }

    /// <summary>Sorted, fully-expanded text form of the record-link half of an install
    /// baseline — see TenantStoragePatches.DescribeInstallBaseline for why this exists.</summary>
    internal static IEnumerable<string> DescribeInstallBaseline(object? snapshot)
    {
        if (snapshot is not ValueTuple<Dictionary<NavRecordId, List<Entry>>, int> state)
            return new[] { "link|<none>" };
        return state.Item1
            .SelectMany(pair => pair.Value.Select(e =>
                $"link|{Convert.ToHexString(pair.Key.GetBytes())}|{e.Id}|{e.Url}|{e.Description}"))
            .Append($"link-next-id|{state.Item2}")
            .OrderBy(x => x, StringComparer.Ordinal);
    }

    /// <summary>Counterpart of <see cref="SerializeInstallBaseline"/>, producing exactly the
    /// tuple shape <see cref="CaptureInstallBaseline"/> produces.</summary>
    internal static object? DeserializeInstallBaseline(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0) return null;
        var links = new Dictionary<NavRecordId, List<Entry>>(EqualityComparer<NavRecordId>.Default);
        for (var i = 0; i < count; i++)
        {
            var idBytes = r.ReadBytes(r.ReadInt32());
            var recordId = NavRecordId.CreateFromBytes(idBytes, 0, idBytes.Length);
            var entryCount = r.ReadInt32();
            var entries = new List<Entry>(entryCount);
            for (var j = 0; j < entryCount; j++)
                entries.Add(new Entry(r.ReadInt32(), r.ReadString(), r.ReadString()));
            links[recordId] = entries;
        }
        var nextId = r.ReadInt32();
        return (links, nextId);
    }

    // TEMPORARY (memory-census diagnostic) — total link entries across all records.
    // See MemoryCensus.cs.
    internal static int CensusEntryCount()
    {
        lock (_lock)
            return _links.Values.Sum(l => l.Count);
    }

    public static void Register(Assembly navNcl)
    {
        var tNavRecord = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecord");
        if (tNavRecord == null)
        {
            Console.Error.WriteLine("[RecordLinkPatches] NavRecord type not found; skipping");
            return;
        }

        var me = typeof(RecordLinkPatches);
        int hooks = 0;

        void HookIf(MethodInfo? original, string replacementName, string description)
        {
            if (original == null)
            {
                Console.Error.WriteLine($"[RecordLinkPatches] skip {description}: original method not found");
                return;
            }
            var repl = me.GetMethod(replacementName,
                BindingFlags.Public | BindingFlags.Static);
            if (repl == null) return;
            try
            {
                JmpHook.Apply(original, repl, description);
                hooks++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordLinkPatches] hook {description} failed: {ex.Message}");
            }
        }

        // ALHasLinks (property getter) — bool
        HookIf(tNavRecord.GetProperty("ALHasLinks", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(),
               nameof(NavRecord_get_ALHasLinks), "NavRecord.get_ALHasLinks");

        // ALAddLink(url) → int
        HookIf(tNavRecord.GetMethod("ALAddLink", new[] { typeof(string) }),
               nameof(NavRecord_ALAddLink1), "NavRecord.ALAddLink(string)");

        // ALAddLink(url, description) → int
        HookIf(tNavRecord.GetMethod("ALAddLink", new[] { typeof(string), typeof(string) }),
               nameof(NavRecord_ALAddLink2), "NavRecord.ALAddLink(string, string)");

        // ALDeleteLinks() → void
        HookIf(tNavRecord.GetMethod("ALDeleteLinks", Type.EmptyTypes),
               nameof(NavRecord_ALDeleteLinks), "NavRecord.ALDeleteLinks()");

        // ALDeleteLink(int) → void
        HookIf(tNavRecord.GetMethod("ALDeleteLink", new[] { typeof(int) }),
               nameof(NavRecord_ALDeleteLink), "NavRecord.ALDeleteLink(int)");

        // ALCopyLinks(NavRecord) → void
        HookIf(tNavRecord.GetMethod("ALCopyLinks", new[] { tNavRecord }),
               nameof(NavRecord_ALCopyLinks), "NavRecord.ALCopyLinks(NavRecord)");

        // AL also emits direct static `RecordLink.HasLinks(Rec)` for `Rec.HasLinks()`
        // — confirmed via Codeunit*.HasLinks_Scope stack frame pointing straight at
        // the static. Hook it too.
        var tRecordLink = navNcl.GetType("Microsoft.Dynamics.Nav.Runtime.RecordLink");
        if (tRecordLink != null)
        {
            HookIf(tRecordLink.GetMethod("HasLinks",
                       BindingFlags.Public | BindingFlags.Static,
                       binder: null, types: new[] { tNavRecord }, modifiers: null),
                   nameof(RecordLink_HasLinks), "RecordLink.HasLinks(NavRecord)");
        }

        Console.Error.WriteLine($"[RecordLinkPatches] hooked {hooks} link method(s)");
    }

    // ── Replacements ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavRecord_get_ALHasLinks(NavRecord self)
        => HasLinksImpl(self);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool RecordLink_HasLinks(NavRecord record)
        => HasLinksImpl(record);

    private static bool HasLinksImpl(NavRecord rec)
    {
        if (rec == null) return false;
        var key = rec.ALRecordId;
        lock (_lock)
        {
            return _links.TryGetValue(key, out var list) && list.Count > 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavRecord_ALAddLink1(NavRecord self, string url)
        => AddLink(self, url, string.Empty);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavRecord_ALAddLink2(NavRecord self, string url, string description)
        => AddLink(self, url, description);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_ALDeleteLinks(NavRecord self)
    {
        var key = self.ALRecordId;
        lock (_lock)
        {
            _links.Remove(key);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_ALDeleteLink(NavRecord self, int linkId)
    {
        var key = self.ALRecordId;
        lock (_lock)
        {
            if (_links.TryGetValue(key, out var list))
            {
                list.RemoveAll(e => e.Id == linkId);
                if (list.Count == 0) _links.Remove(key);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_ALCopyLinks(NavRecord self, NavRecord fromRecord)
    {
        if (fromRecord == null) return;
        var srcKey = fromRecord.ALRecordId;
        var dstKey = self.ALRecordId;
        lock (_lock)
        {
            if (!_links.TryGetValue(srcKey, out var srcList) || srcList.Count == 0)
                return;
            // Snapshot in case src == dst (caller copied a record onto itself).
            var snapshot = srcList.ToArray();
            if (!_links.TryGetValue(dstKey, out var dstList))
            {
                dstList = new List<Entry>();
                _links[dstKey] = dstList;
            }
            foreach (var src in snapshot)
                dstList.Add(new Entry(++_nextId, src.Url, src.Description));
        }
    }

    private static int AddLink(NavRecord self, string url, string description)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(description);
        if (url.Length > 2048)
            throw new InvalidOperationException("RecordLink URL exceeds 2048 character limit");
        var key = self.ALRecordId;
        lock (_lock)
        {
            if (!_links.TryGetValue(key, out var list))
            {
                list = new List<Entry>();
                _links[key] = list;
            }
            var id = ++_nextId;
            list.Add(new Entry(id, url, description));
            return id;
        }
    }
}
