// MediaSetPatches — in-memory backing for NavMediaSet AL methods (PAGE-REPORT-CLUSTERS §4).
//
// BC's NavMediaSet.ALInsert / ALRemove / ALItem / get_ALCount all reach the database /
// Session tier which is not present in the runner. So does the shared internal
// AddMediaToSetAsync(NavSession, Guid setId, Guid mediaId) helper that every AL-facing
// import/insert path funnels through — see below.
//
// ROOT CAUSE HISTORY (issue #1773)
//   The original design keyed the membership list on (ParentRecord, FieldNo) — the
//   NavRecord instance that owns the field, plus the field number — via a
//   ConditionalWeakTable, reasoning that NavRecord.GetFieldValueSafe hands out a fresh
//   NavMediaSet wrapper on every field access but the SAME ParentRecord instance for the
//   SAME AL record variable, and that the NavGuid Key can't be used because every
//   never-touched field starts at Guid.Empty (so all such fields would collide on one
//   bucket).
//
//   That reasoning is only half the story, and both halves turned out to be independently
//   broken:
//
//   1. MediaSet.ImportStream() compiles to NavMediaSet.ALImport(DataError, NavStream,
//      string[, string[, string]]) — an overload this file's Cecil rewrite (see
//      NclCecilRewrite.cs) never intercepted. Only the file-based ALImport(DataError,
//      string, string[, string]) overloads (ImportFile) were rewritten. So
//      ImportStream's REAL, unmodified body ran — which is fine for the first half (it
//      calls the same NavMediaImport.ImportMediaObjectAsync content-storage path the
//      working "Media" (single) field control case already uses, so the returned MediaId
//      is real and the bytes really land in the Media/TenantMedia platform table — see
//      RecordPatches.PlatformMediaTables.cs) but then calls
//      NavMediaSet.AddMediaToSetAsync(session, setId, mediaId) to record membership. That
//      method's real body reaches into an UNDECLARED platform table (the id from
//      Media.NavMediaHelper.GetMediaSetTableId(ParentId), analogous to
//      GetMediaTableId(ParentId) for Media/TenantMedia, but never declared here) via
//      NavSession.GetGlobalRecordInstance + ALGetAsync/ALInsertAsync. Its own body
//      discards ALInsertAsync's success/failure (the IL literally `pop`s the result), so
//      the failure is silent: ImportStream returns a real, non-null MediaId and never
//      throws, but the membership row never lands anywhere IsInsert/get_ALCount/ALItem
//      (which were never reached in the first place — those hooks are only wired for
//      ALInsert(DataError,Guid), the *explicit* Insert(Guid) surface) can see.
//
//   2. Even the explicit Insert(Guid) surface — which DOES route through this file's
//      ALInsert patch — didn't survive a fresh Get() into a SECOND AL record variable of
//      the same row: (ParentRecord, FieldNo) is a different NavRecord instance for
//      `Row2.Get(...)` than the one `Row1.Insert()` populated, even though both refer to
//      the same underlying database row (confirmed empirically: same NavRecord.SystemId,
//      different ParentRecord object identity/hash). So the ConditionalWeakTable bucket
//      the membership was added to is simply never consulted again.
//
// THE FIX
//   Both problems collapse into the same fix once you look at what BC's own real body
//   does with AddMediaToSetAsync's return value: the caller (ALImportAsync's async state
//   machine) takes the Guid AddMediaToSetAsync hands back and calls
//   NavMediaValueBase.SaveValueToTableField(Guid) with it — the REAL, unmodified method
//   that writes the "media set id" into the record's own field bytes. That field write is
//   what should make a MediaSet's identity durable across Modify()/Get(), exactly like any
//   other field. So instead of inventing our own persistence keyed on transient .NET
//   object identity, we cooperate with BC's real mechanism:
//
//     * NavMediaSet.AddMediaToSetAsync is now Cecil-rewritten too (previously untouched),
//       to NavMediaSet_AddMediaToSetAsync below. It generates a fresh container Guid when
//       none exists yet, records the (setId -> mediaId) membership in an in-memory store
//       keyed purely by that Guid, and returns it — letting the REAL, unmodified caller
//       persist it into the field via SaveValueToTableField exactly as BC intends. Because
//       everything upstream of AddMediaToSetAsync in ALImportAsync's real body keeps
//       running untouched, ImportStream's content storage (the actual bytes) is 100% real
//       BC code, not a runner re-implementation — the retrieved MediaId round-trips
//       through the real Media/TenantMedia table.
//
//     * ALInsert(DataError, Guid) — the explicit Insert(Guid) surface, which has no real
//       body left to lean on (BC's own AddMediaToSetAsync-based flow is import-only) —
//       does the same thing itself: generate+save the container Guid on first use, add
//       to the SAME Guid-keyed store.
//
//     * ALRemove / get_ALCount / ALItem read the container Guid straight off
//       NavMediaValueBase.Key.Value (the REAL field value, populated by
//       SaveValueToTableField above) and look it up in the SAME store — so any NavRecord
//       instance that re-reads the same row sees the same membership, because the lookup
//       key is the row's own persisted field content, not anything about which .NET object
//       happened to read it.
//
//   Store keying is therefore (once established) a real, globally-unique per-field Guid —
//   never the transient ParentRecord instance — so it survives Modify()+Get(), a second
//   AL record variable pointed at the same row, or any other re-materialisation BC's own
//   field storage would survive.
//
// LIFETIME
//   The old ConditionalWeakTable bought automatic cleanup when a NavRecord became
//   unreachable, at the cost of being the actual bug (see above). A plain
//   Guid-keyed ConcurrentDictionary doesn't get that for free, but MediaSet membership is
//   per-test mutable state exactly like RecordLinkPatches' polyfill store or
//   TenantStoragePatches' isolated-storage store — real BC rolls it back at the end of
//   every test's transaction. ResetForTest() below is wired into
//   RecordPatches.ResetPerTestState() (called before every test method — see
//   TestExecutor.cs) so the store is cleared at the same boundary those other
//   per-test stores already use. That reclaims memory deterministically every test,
//   rather than "eventually, whenever the GC gets around to the abandoned NavRecord" —
//   strictly better than what the ConditionalWeakTable gave us, and no NavRecord/
///  NavMediaSet instance is ever kept alive by this store (the key is a value-type Guid).
//
// ALImport (ImportFile) is left exactly as before: a full-body replacement returning a
// fresh Guid with no real content storage. That's an existing, separately-scoped decision
// (see the file overloads below) — not something #1773 asked for or touched.
//
// Hook installation: BcRuntime.cs ApplyNavMediaSetPatches block + NclCecilRewrite.cs
// "Batch 5" block.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AlRunner.Patches;

public static class MediaSetPatches
{
    // Membership store: keyed purely on the MediaSet field's own container Guid (what
    // NavMediaValueBase.Key.Value holds once anything has been added). See file header —
    // this Guid is BC's own real per-field value, round-tripped through the record's field
    // bytes by the unmodified SaveValueToTableField, so it is stable across any
    // re-materialisation of the owning record.
    private static readonly ConcurrentDictionary<Guid, List<Guid>> _bySetId = new();

    /// <summary>Per-test reset — see LIFETIME in the file header. Called from
    /// RecordPatches.ResetPerTestState() before every test method.</summary>
    public static void ResetForTest() => _bySetId.Clear();

    // Lazy-initialized reflectors for NavMediaValueBase members.
    private static PropertyInfo? _parentRecordProp;
    private static PropertyInfo? _fieldNoProp;
    private static PropertyInfo? _keyProp;      // NavGuid Key { get; }
    private static PropertyInfo? _navGuidValueProp; // Guid NavGuid.Value { get; }
    private static MethodInfo? _saveValueMethod; // void SaveValueToTableField(Guid)

    private static void EnsureReflectors(object self)
    {
        if (_parentRecordProp != null) return;
        var t = self.GetType();
        _parentRecordProp = t.GetProperty("ParentRecord",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            ?? t.BaseType?.GetProperty("ParentRecord",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        _fieldNoProp = t.GetProperty("FieldNo",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            ?? t.BaseType?.GetProperty("FieldNo",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var keyProp = t.GetProperty("Key", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            ?? t.BaseType?.GetProperty("Key", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        _keyProp = keyProp;
        if (keyProp != null)
            _navGuidValueProp = keyProp.PropertyType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        _saveValueMethod = t.GetMethod("SaveValueToTableField",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
            binder: null, types: new[] { typeof(Guid) }, modifiers: null)
            ?? t.BaseType?.GetMethod("SaveValueToTableField",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy,
                binder: null, types: new[] { typeof(Guid) }, modifiers: null);
    }

    /// <summary>The MediaSet field's own real container Guid (NavMediaValueBase.Key.Value),
    /// or Guid.Empty if nothing has ever been added to this field yet.</summary>
    private static Guid GetContainerGuid(object self)
    {
        EnsureReflectors(self);
        var key = _keyProp?.GetValue(self);
        if (key == null || _navGuidValueProp == null) return Guid.Empty;
        return _navGuidValueProp.GetValue(key) is Guid g ? g : Guid.Empty;
    }

    /// <summary>Persists a newly established container Guid into the record's own field
    /// bytes via the real (unmodified) NavMediaValueBase.SaveValueToTableField — the same
    /// call BC's own ALImportAsync body makes with AddMediaToSetAsync's result. Faithful:
    /// this is BC's own method, not a re-implementation.</summary>
    private static void SaveContainerGuid(object self, Guid guid)
    {
        EnsureReflectors(self);
        _saveValueMethod?.Invoke(self, new object[] { guid });
    }

    private static List<Guid> GetOrCreateList(Guid setId) => _bySetId.GetOrAdd(setId, static _ => new List<Guid>());

    // ── ALInsert(DataError errorLevel, Guid mediaId) → bool ─────────────────────────────
    // The explicit Insert(MediaId) AL surface — inserts an EXISTING media id (e.g. one
    // obtained from another record's Item()) into this field's set. Has no real BC body to
    // lean on (BC's real add-to-set flow is import-only, via AddMediaToSetAsync below), so
    // this establishes+saves the container Guid itself on first use.

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool NavMediaSet_ALInsert(object self, object errorLevel, Guid mediaId)
    {
        var setId = GetContainerGuid(self);
        if (setId == Guid.Empty)
        {
            setId = Guid.NewGuid();
            SaveContainerGuid(self, setId);
        }
        var list = GetOrCreateList(setId);
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
        var setId = GetContainerGuid(self);
        if (setId == Guid.Empty) return false; // nothing was ever inserted — matches BC.
        var list = GetOrCreateList(setId);
        lock (list)
            return list.Remove(mediaId);
    }

    // ── get_ALCount() → int ──────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavMediaSet_get_ALCount(object self)
    {
        var setId = GetContainerGuid(self);
        if (setId == Guid.Empty) return 0;
        var list = GetOrCreateList(setId);
        lock (list)
            return list.Count;
    }

    // ── ALItem(int index) → Guid  (1-based, per BC AL convention) ───────────────────────

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_ALItem(object self, int index)
    {
        var setId = GetContainerGuid(self);
        if (setId == Guid.Empty) return Guid.Empty;
        var list = GetOrCreateList(setId);
        lock (list)
            return (index >= 1 && index <= list.Count) ? list[index - 1] : Guid.Empty;
    }

    // ── AddMediaToSetAsync(NavSession, Guid setId, Guid mediaId) → ValueTask<Guid>  (BC 28+)
    // AddMediaToSet(Guid setId, Guid mediaId) → Guid                    (BC 27.x, synchronous)
    //
    // The shared internal helper EVERY AL-facing MediaSet import path (ImportStream,
    // ImportFile's real body if ever un-faked) funnels through in real BC — see file
    // header. `setId` is the field's CURRENT container Guid (Guid.Empty if this is the
    // first item ever added); we generate one if needed, record the membership, and hand
    // the (possibly new) container Guid back so the REAL, unmodified caller can persist it
    // via SaveValueToTableField exactly as BC does. Content storage (the actual bytes)
    // happens entirely in BC's own unmodified code before this is ever called — we only
    // replace the membership bookkeeping BC's real body cannot do without a "Media Set"
    // platform table this runner doesn't declare.
    //
    // BC 27.x HAS NO ASYNC SURFACE AT ALL on NavMediaSet (issue #1802, verified by
    // decompiling Microsoft.Dynamics.Nav.Ncl.dll from the 27.0.38460.53552 and
    // 28.1.49838.50794 cached service tiers with ilspycmd):
    //
    //   BC 27.0: `internal Guid AddMediaToSet(Guid setId, Guid mediaId)` — synchronous, no
    //            NavSession parameter, plain Guid return. Its real body is otherwise
    //            IDENTICAL logic to the 28.x async version below (generate a fresh setId
    //            when empty, ALGet/ALInsert against the same undeclared "Media Set"
    //            platform table, return setId). Callers on 27.x:
    //              - `ALImport(DataError, NavStream, string, string, string)` (the 5-param
    //                overload every ImportStream/ImportFile overload funnels into) calls
    //                `guid = AddMediaToSet(base.Key.Value, mediaId);` synchronously, then
    //                `SaveValueToTableField(guid)` — exactly the pattern the file header
    //                describes for 28.x's ALImportAsync, just without the awaits.
    //              - `ALInsert(DataError, Guid)` also calls `AddMediaToSet` in its real
    //                body, but that doesn't matter here: NavMediaSet_ALInsert below is a
    //                full-body replacement on BOTH versions (BC's own Insert flow has no
    //                real body worth leaning on — see file header — so we never reach BC's
    //                real ALInsert on either version, hence never reach AddMediaToSet via
    //                that path on 27.x either).
    //   BC 28.x: `internal async ValueTask<Guid> AddMediaToSetAsync(NavSession session,
    //            Guid setId, Guid mediaId)` — the version this file originally targeted.
    //
    // Both shapes are handled by the two helpers below via the SAME shared membership
    // bookkeeping (AddToSetCore), so 27.x and 28.x observe identical AL-visible behaviour.
    //
    // The sibling 27.x-only members `RemoveMediaRecord(Guid) -> bool` and
    // `GetContainedMedia() -> List<Guid>` do NOT need their own hooks: they are only
    // reachable from BC's real `ALRemove`/`CountMediaInSet`/`UpdateMediaCache` bodies, and
    // ALRemove/get_ALCount/ALItem are ALREADY full-body Cecil replacements on both versions
    // (NavMediaSet_ALRemove / NavMediaSet_get_ALCount / NavMediaSet_ALItem below) — so BC's
    // real bodies that would call RemoveMediaRecord/GetContainedMedia are never executed on
    // either version, and those two internal methods are simply dead code from the runner's
    // perspective. Confirmed by decompiling both versions: their signatures and callers
    // otherwise match 1:1 (RemoveMediaRecordAsync/GetContainedMediaAsync on 28.x).

    private static Guid AddToSetCore(Guid setId, Guid mediaId)
    {
        var effectiveSetId = setId == Guid.Empty ? Guid.NewGuid() : setId;
        var list = GetOrCreateList(effectiveSetId);
        lock (list)
        {
            if (!list.Contains(mediaId))
                list.Add(mediaId);
        }
        return effectiveSetId;
    }

    /// <summary>BC 28.x shape: AddMediaToSetAsync(NavSession, Guid, Guid) → ValueTask&lt;Guid&gt;.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static System.Threading.Tasks.ValueTask<Guid> NavMediaSet_AddMediaToSetAsync(
        object self, object session, Guid setId, Guid mediaId)
    {
        return new System.Threading.Tasks.ValueTask<Guid>(AddToSetCore(setId, mediaId));
    }

    /// <summary>BC 27.x shape: AddMediaToSet(Guid, Guid) → Guid — synchronous, no NavSession
    /// parameter. See the block comment above for why this is its own helper and not a
    /// re-signature of the async one.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_AddMediaToSet(object self, Guid setId, Guid mediaId)
    {
        return AddToSetCore(setId, mediaId);
    }

    // ── ALImport(DataError, string fileName, string description) → Guid ──────────────────
    // Covers the ImportFile(fileName, description) AL overload. Unchanged by #1773: still
    // a full fake with no real content storage (out of scope for this issue — see file
    // header) — but now shares the Guid-keyed membership store so Count()/Item() see it,
    // and returns the container Guid rather than the (fake) media item's own Guid — see
    // NavMediaSet_AddMediaToSetAsync above for why that's what real BC's ALImportAsync
    // actually hands back (confirmed by decompiling the real body: the returned value is
    // compared against/saved as Key, i.e. the set container, not the imported item).

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_ALImport_File2(object self, object errorLevel, string fileName, string description)
    {
        var id = Guid.NewGuid();
        var setId = GetContainerGuid(self);
        if (setId == Guid.Empty)
        {
            setId = Guid.NewGuid();
            SaveContainerGuid(self, setId);
        }
        var list = GetOrCreateList(setId);
        lock (list)
            list.Add(id);
        return setId;
    }

    // ── ALImport(DataError, string fileName, string description, string mimeType) → Guid ─

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_ALImport_File3(object self, object errorLevel, string fileName, string description, string mimeType)
    {
        var id = Guid.NewGuid();
        var setId = GetContainerGuid(self);
        if (setId == Guid.Empty)
        {
            setId = Guid.NewGuid();
            SaveContainerGuid(self, setId);
        }
        var list = GetOrCreateList(setId);
        lock (list)
            list.Add(id);
        return setId;
    }

    // ── ALExport(DataError, string fileBaseName) → int ───────────────────────────────────
    // Returns 0 (no blob data in standalone mode).

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NavMediaSet_ALExport(object self, object errorLevel, string fileBaseName)
    {
        return 0;
    }

    // ── get_ALMediaId() → Guid  (MediaSet container identity) ───────────────────────────
    // Declared on NavMediaValueBase. Real body is `return Key.Value` — decompiling
    // NavMediaSet's real ALImportAsync confirms MediaId()/ImportStream()'s return value
    // and Key.Value are the SAME container Guid (ImportStream literally returns whatever
    // AddMediaToSetAsync/SaveValueToTableField just persisted into Key). Now that
    // AddMediaToSetAsync/ALInsert really populate Key.Value (see #1773 fix above), prefer
    // it here too — anything else would make MediaId() diverge from what ImportStream()
    // just returned, which real BC never does.
    //
    // The ONLY case Key.Value can't answer is a field nothing has ever been added to
    // (Key.Value is Guid.Empty for every such field, real BC included) — the archived test
    // MediaId_ReturnsNonEmptyGuid asserts a non-empty result even then, so that one case
    // keeps the pre-existing (ParentRecord, FieldNo)-keyed fake as a fallback. Whether real
    // BC actually agrees with that expectation on an empty set is unverified (the archived
    // test predates the al-language corpus and was never run against a real service tier);
    // left as-is rather than guessed at, since it's a separate, unopened question from
    // #1773.

    private static readonly ConditionalWeakTable<object, Dictionary<int, Guid>> _mediaIds = new();

    public static Guid GetOrCreateMediaId(object self)
    {
        var real = GetContainerGuid(self);
        if (real != Guid.Empty) return real;

        EnsureReflectors(self);
        var parentRec = _parentRecordProp?.GetValue(self);
        var fieldNo = _fieldNoProp?.GetValue(self) is int fn ? fn : 0;
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
    // container-Guid keys. See MemoryCensus.cs.
    internal static int CensusEntryCount()
    {
        int n = 0;
        foreach (var (_, list) in _bySetId)
            n += list.Count;
        return n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NavMediaSet_get_ALMediaId(object self)
    {
        return GetOrCreateMediaId(self);
    }
}
