// RecordPatches.ObjectInventoryStore — what AllObj (2000000038) and AllObjWithCaption (2000000058)
// share between their in-memory stores: the rows built once per inventory key, each store topped
// up once per key, and a filled store carried across a test-codeunit boundary when it would hold
// exactly what a fresh store would be filled with. See docs/virtual-tables-allobj.md#populate-cost.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>What the rows of one top-up depend on: the inventory's inputs, the executing
    /// app group (<see cref="InAppGroupScope{T}(string, int, T)"/>) and the visible-app set.</summary>
    internal readonly record struct ObjectInventoryKey(ObjectInventoryStamp Stamp, Guid? AppGroup, string Visibility);

    /// <summary>
    /// One term per input <see cref="EnumerateKnownAlObjects"/>, <see cref="IsHiddenFromCurrentAppGroup"/>
    /// and <see cref="BuildObjectOwnerIndex"/> read, plus the caption registries AllObjWithCaption
    /// reads through <see cref="SourceCaptionFor"/>. A reload bumps an epoch; within one bundle the
    /// registries only grow, which a count sees.
    /// <para>Trap: a new source read by any of those needs a term here, or the two tables stop
    /// listing what it adds until something else moves (#4851).</para>
    /// </summary>
    internal readonly record struct ObjectInventoryStamp(
        int BundleEpoch, int BcAppEpoch, int AppGroupGeneration, int ModuleAssemblies, int EnumVersion,
        int Tables, int Pages, int PageExtensions, int Reports, int ReportExtensions, int Queries, int XmlPorts,
        int ObjectDecls, int ObjectDeclOwners, int SourceOwners, int AmbiguousSource, int SourceDeclarers,
        int ParsedByAppGroup, int AppGroupsSharingAnId, int ObjectCaptions, int CaptionsByAppGroup);

    private static ObjectInventoryStamp CurrentObjectInventoryStamp() => new(
        AlRunner.BcRuntime.CurrentBundleEpoch,
        BcAppRegistrationEpoch,
        System.Threading.Volatile.Read(ref _appGroupRegistrationGeneration),
        AlRunner.BcRuntime.RegisteredModuleAssemblyCount,
        AlEnumMetadataRegistry.Version,
        _parsedTables.Count, _parsedPages.Count, _parsedPageExtensions.Count, _parsedReports.Count,
        _parsedReportExtensions.Count, _parsedQueries.Count, _parsedXmlPorts.Count,
        _parsedObjectDecls.Count, _parsedObjectDeclOwners.Count,
        _sourceObjectOwners.Count, _ambiguousSourceObjects.Count, _sourceObjectDeclarers.Count,
        _parsedByAppGroup.Count, _appGroupsSharingAnId.Count,
        _parsedObjectCaptions.Count, _captionByAppGroup.Count);

    private static string VisibilityKey(HashSet<Guid>? visibleApps)
        => visibleApps == null ? "*" : string.Join(",", visibleApps.OrderBy(g => g));

    // Values is the finished row, shared by every store it is copied into — see InsertSharedRow.
    private readonly record struct ObjectInventoryRow(int TypeOrdinal, int Id, Array Values);

    /// <summary>Builds one row's value array for the table being populated.</summary>
    private delegate Array ObjectInventoryRowBuilder(
        string kind, int id, string name, string? caption, string? subtype, string normalizedKind,
        int typeOrdinal, Guid owningAppId);

    // Rows per (table, app group, visibility) for ONE stamp; a new stamp drops them all. A class
    // holding the metatable as `object`, never a tuple: a static whose signature names a value
    // type with a Nav field loads CodeAnalysis before --bc-version is parsed
    // (ProgramMustNotNameNavCaValueTypesTests).
    private sealed class ObjectInventoryEntry
    {
        public required object MetaTable;
        public required ObjectInventoryRow[] Rows;
    }
    private static readonly object _oiLock = new();
    private static ObjectInventoryStamp? _oiStamp;
    private static readonly Dictionary<(int TableId, Guid? AppGroup, string Visibility), ObjectInventoryEntry> _oiRows = new();

    /// <summary>Per in-memory provider: the keys it has taken rows for, the metatable they were
    /// laid out by, and whether AL has written to its table since.</summary>
    private sealed class ObjectInventoryStoreState
    {
        public readonly HashSet<ObjectInventoryKey> Applied = new();
        public object? MetaTable;
        public int TableId;
        public bool Written;
    }
    private static readonly ConditionalWeakTable<object, ObjectInventoryStoreState> _oiStoreState = new();

    /// <summary>Dropped with the parsed registries on a reload; the stamp would miss anyway.</summary>
    internal static void ResetObjectInventoryMemo()
    {
        lock (_oiLock)
        {
            _oiRows.Clear();
            _oiStamp = null;
        }
        _oiParked.Clear();
    }

    /// <summary>
    /// Top <paramref name="provider"/> up with the rows of the current inventory key, once per key.
    /// Later handouts under a key the store already took return without touching the inventory.
    /// </summary>
    private static void PopulateObjectInventoryStore(
        int tableId, string perfName, string scopeLabel, object provider, NCLMetaTable metaTable,
        Dictionary<string, int> ordinals, ConcurrentDictionary<(int, int), byte> done, ObjectInventoryRowBuilder build)
    {
        var visibleApps = PinInventoryScope(provider, scopeLabel);
        var key = new ObjectInventoryKey(CurrentObjectInventoryStamp(), CurrentAppGroupAppId(), VisibilityKey(visibleApps));
        PerfTrace.Log($"{perfName}.Handout");
        var state = _oiStoreState.GetValue(provider, static _ => new ObjectInventoryStoreState());
        lock (state)
        {
            state.MetaTable ??= metaTable;
            state.TableId = tableId;
            if (state.Applied.Contains(key)) return;
        }

        var inserted = 0;
        foreach (var row in ObjectInventoryRowsFor(tableId, perfName, key, metaTable, ordinals, visibleApps, build))
        {
            if (!done.TryAdd((row.TypeOrdinal, row.Id), 0))
                continue;
            InsertSharedRow(provider, metaTable, row.Values);
            inserted++;
        }
        PerfTrace.Log($"{perfName}.TopUp {inserted} row(s)");
        // After the inserts, so a throw above leaves the key unapplied and the next handout retries.
        lock (state) state.Applied.Add(key);
    }

    /// <summary>
    /// Insert one built row. The array is copied, its NavValues are not: they are immutable, and
    /// BC shares them the same way — AddSystemFieldValues puts one static VirtualTimeStamp and
    /// NavDateTime.Default/NavGuid.Default into every virtual row, and CloneValues restores
    /// install-baseline rows by the same shallow copy (#4851).
    /// </summary>
    private static void InsertSharedRow(object provider, NCLMetaTable metaTable, Array builtValues)
        => InsertVirtualRowValues(provider, metaTable, (Array)builtValues.Clone());

    private static ObjectInventoryRow[] ObjectInventoryRowsFor(
        int tableId, string perfName, ObjectInventoryKey key, NCLMetaTable metaTable,
        Dictionary<string, int> ordinals, HashSet<Guid>? visibleApps, ObjectInventoryRowBuilder build)
    {
        if (TryGetMemoizedRows(tableId, key, metaTable) is { } hit)
            return hit;

        var rows = new List<ObjectInventoryRow>();
        var seen = new HashSet<(int, int)>();
        // #3117: the owner index is built on the first row, never for an empty walk.
        Dictionary<(string Kind, int Id), Guid>? ownerIndex = null;

        foreach (var (kind, id, name, caption, subtype) in EnumerateKnownAlObjects())
        {
            if (id <= 0 || IsHiddenFromCurrentAppGroup(kind, id, visibleApps)) continue;
            var normalized = NormalizeObjectTypeName(kind);
            if (!ordinals.TryGetValue(normalized, out var typeOrdinal))
                // This AL object kind has no ordinal in THIS BC version's option set. Real BC
                // would not list it either — skipping is faithful, inventing an ordinal is not.
                continue;
            // First registry to list (type, id) names it, as the per-provider set did.
            if (!seen.Add((typeOrdinal, id)))
                continue;
            // The owning app, from the declaring .app's SymbolReference.json or the emitted
            // assembly. NOT a fallback to the current bundle: an unknown owner stays Guid.Empty
            // and fails the ownership check, which is what a wrong guess should look like.
            ownerIndex ??= BuildObjectOwnerIndex();
            var owningAppId = ownerIndex.TryGetValue((normalized, id), out var owner) ? owner : Guid.Empty;
            rows.Add(new ObjectInventoryRow(typeOrdinal, id,
                build(kind, id, name, caption, subtype, normalized, typeOrdinal, owningAppId)));
        }

        var built = rows.ToArray();
        PerfTrace.Log($"{perfName}.InventoryWalk {built.Length} row(s)");
        // Published only once the walk completed (#3143 throws out of it), and only under the
        // stamp it was keyed on: a registry that grew during the walk re-walks next time.
        lock (_oiLock)
        {
            if (_oiStamp != key.Stamp)
            {
                _oiRows.Clear();
                _oiStamp = key.Stamp;
            }
            _oiRows[(tableId, key.AppGroup, key.Visibility)] = new ObjectInventoryEntry { MetaTable = metaTable, Rows = built };
        }
        return built;
    }

    private static ObjectInventoryRow[]? TryGetMemoizedRows(int tableId, ObjectInventoryKey key, object metaTable)
    {
        lock (_oiLock)
            return _oiStamp == key.Stamp && _oiRows.TryGetValue((tableId, key.AppGroup, key.Visibility), out var hit)
                   && ReferenceEquals(hit.MetaTable, metaTable)
                ? hit.Rows
                : null;
    }

    // ── Carrying a filled store across a test-codeunit boundary (#4859) ─────────────────────────

    // Per DataAccessSource: table id -> stores parked at the last boundaries, newest last.
    private static readonly ConditionalWeakTable<object, Dictionary<int, List<object>>> _oiParked = new();
    private const int MaxParkedStoresPerTable = 4;

    private static bool IsObjectInventoryTable(int tableId)
        => tableId is AllObjVirtualTableId or AllObjWithCaptionVirtualTableId;

    /// <summary>
    /// A write to AllObj or AllObjWithCaption, before it lands: every store of that table stops
    /// being reusable. Called from <see cref="NoteTransactionWriteForTable"/>, which every AL write
    /// entry point reaches (ALDatabasePatches.NoteRecordWrite).
    /// </summary>
    private static void NoteObjectInventoryWrite(int tableId)
    {
        if (!IsObjectInventoryTable(tableId)) return;
        foreach (var (_, perTable) in _dataAccessByTable)
            if (perTable.TryGetValue(tableId, out var dataAccess)
                && ProviderOf(dataAccess) is { } provider
                && _oiStoreState.TryGetValue(provider, out var state))
                lock (state) state.Written = true;
    }

    /// <summary>At a boundary, keep a filled store that no AL write has touched, instead of
    /// dropping it. Whether it is handed out again is decided at the next handout.</summary>
    internal static void ParkObjectInventoryStore(object source, int tableId, object dataAccess)
    {
        if (!IsObjectInventoryTable(tableId)) return;
        if (ProviderOf(dataAccess) is not { } provider || !_oiStoreState.TryGetValue(provider, out var state)) return;
        lock (state)
            if (state.Written || state.Applied.Count == 0) return;
        var byTable = _oiParked.GetValue(source, static _ => new Dictionary<int, List<object>>());
        lock (byTable)
        {
            if (!byTable.TryGetValue(tableId, out var list)) byTable[tableId] = list = new List<object>();
            list.Add(dataAccess);
            if (list.Count > MaxParkedStoresPerTable) list.RemoveAt(0);
        }
    }

    /// <summary>
    /// A parked store whose rows are exactly the rows a fresh store would receive on this
    /// handout, or null. See <see cref="IsReusableObjectInventoryStore"/> for the rule.
    /// </summary>
    private static object? TakeParkedObjectInventoryStore(object source, NCLMetaTable table,
        Func<object, ConcurrentDictionary<(int, int), byte>> doneSetOf, string perfName)
    {
        if (!_oiParked.TryGetValue(source, out var byTable)) return null;
        List<object>? list;
        lock (byTable)
            if (!byTable.TryGetValue(table.TableId, out list) || list.Count == 0) return null;

        var stamp = CurrentObjectInventoryStamp();
        var appGroup = CurrentAppGroupAppId();
        lock (byTable)
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var dataAccess = list[i];
                if (ProviderOf(dataAccess) is not { } provider
                    || !_oiStoreState.TryGetValue(provider, out var state)
                    || !_inventoryScopeByProvider.TryGetValue(provider, out var scope)
                    || scope.AppId != appGroup)
                    continue;
                // The visible set this handout's PinInventoryScope would compute for that store.
                var key = new ObjectInventoryKey(stamp, appGroup, VisibilityKey(WidenForExecutingApps(scope.VisibleApps)));
                var rows = TryGetMemoizedRows(table.TableId, key, table);
                if (rows == null) continue;
                bool reusable;
                lock (state)
                    reusable = IsReusableObjectInventoryStore(state.Written, state.Applied,
                        doneSetOf(provider).Count, key, rows.Length, ReferenceEquals(state.MetaTable, table));
                if (!reusable) continue;
                list.RemoveAt(i);
                PerfTrace.Log($"{perfName}.Reuse");
                return dataAccess;
            }
        }
        return null;
    }

    /// <summary>
    /// A parked store may stand in for a fresh one on a handout under <paramref name="current"/>
    /// only when a fresh store would end up with the very same rows: nothing written, laid out by
    /// the same metatable, filled only under the current stamp (a store from an older stamp may
    /// carry an owner that has since become known), and already holding the current key's rows and
    /// NOTHING else. The last is what keeps a store widened by another app's call stack from
    /// answering a narrower read: its rows are a strict superset, so its count is larger.
    /// </summary>
    internal static bool IsReusableObjectInventoryStore(
        bool written, IReadOnlyCollection<ObjectInventoryKey> applied, int storedRows,
        ObjectInventoryKey current, int currentRows, bool sameMetaTable)
        => !written
           && sameMetaTable
           && applied.Contains(current)
           && applied.All(k => k.Stamp == current.Stamp)
           && storedRows == currentRows;

    private static object? ProviderOf(object dataAccess)
        => _pDataAccessDataProvider?.GetValue(dataAccess);
}
