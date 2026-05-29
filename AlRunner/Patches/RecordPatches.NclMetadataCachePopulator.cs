// RecordPatches.NclMetadataCachePopulator — lazy-populates the skeleton
// NCLMetadata.metadataCacheEntries[Table] dictionary from parsed AL sources, so
// that NavGlobal.NCLMetadata.GetMetaTableById / GetMetaApplicationObject
// callers find a real NCLMetaTable instead of throwing
// NavNCLApplicationObjectNotFoundException.
//
// Sequence:
//   1. BcRuntime.InjectSkeletonSystemTenant builds a skeleton NCLMetadata and
//      pre-allocates empty ConcurrentDictionary entries per ObjectType.
//   2. RecordPatches.Register() parses every .al source dir registered so far
//      via TryParseTableFile → _parsedTables.
//   3. After ParseAllSources, this populator iterates _parsedTables, calls the
//      existing BuildNCLMetaTable(int) factory (which uses NCLMetaTable's
//      internal CreateFromMetaTable), wraps each result in
//      NCLMetadataCacheEntry.CreateWithBase, and inserts it into the skeleton
//      cache dictionary at metadataCacheEntries[(int)ObjectType.Table].
//   4. Subsequent AddSourceDir calls (which parse on-demand when _registered)
//      also feed the cache so per-suite tests see their own tables.
//
// This is the §O follow-up to §N — §N populated empty cache arrays so the
// failure mode shifted from NRE → NavNCLApplicationObjectNotFoundException;
// §O fills those arrays with real entries built from AL source.
using System.Collections.Concurrent;
using System.Reflection;
using AlRunnerV2.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunnerV2.Patches;

public static partial class RecordPatches
{
    private static Type? _tNCLMetadataCacheEntry;
    private static MethodInfo? _mCreateWithBase;
    private static FieldInfo? _fNCLMetadataCacheEntries;   // NCLMetadata.metadataCacheEntries
    private static FieldInfo? _fNCLMetaAppObjMetadataLoaded; // NCLMetaApplicationObject.metadataLoaded

    /// <summary>
    /// Populate the skeleton NCLMetadata's cache with one entry per parsed AL table.
    /// Idempotent — duplicates are skipped via TryAdd.
    /// </summary>
    internal static void PopulateNclMetadataCache()
    {
        var skeleton = BcRuntime.SkeletonNCLMetadata;
        if (skeleton == null)
        {
            // Skeleton NCLMetadata wasn't built (env-ctor fallback path). No cache to fill.
            return;
        }

        EnsureCachePopulatorReflection();
        if (_fNCLMetadataCacheEntries == null || _mCreateWithBase == null) return;

        // metadataCacheEntries is a ConcurrentDictionary<int, NCLMetadataCacheEntry>[]
        // — indexes correspond to Microsoft.Dynamics.Nav.Types.ObjectType:
        //   Table=1, Report=3, Page=8.
        var arr = _fNCLMetadataCacheEntries.GetValue(skeleton) as Array;
        if (arr == null) return;

        const int objectTypeTable = 1;
        const int objectTypeReport = 3;
        const int objectTypeXmlPort = 6;
        const int objectTypePage = 8;
        const int objectTypeQuery = 9;

        // Tables — existing §O path.
        PopulateOneObjectType(arr, objectTypeTable, _parsedTables.Keys.ToArray(),
            id => _metaTableCache.GetOrAdd(id, BuildNCLMetaTable), "Table");

        // Pages — §P, mirror via BuildNCLMetaForm using NCLMetaForm.CreateEmptyNCLMetaForm.
        PopulateOneObjectType(arr, objectTypePage, _parsedPages.Keys.ToArray(),
            id => _metaFormCache.GetOrAdd(id, BuildNCLMetaForm), "Page");

        // Reports — §P, mirror via BuildNCLMetaReport using NCLMetaReport.CreateEmptyNCLMetaReport.
        PopulateOneObjectType(arr, objectTypeReport, _parsedReports.Keys.ToArray(),
            id => _metaReportCache.GetOrAdd(id, BuildNCLMetaReport), "Report");

        // Queries — same shape, ObjectType=9, factory takes ApplicationObjectId.
        PopulateOneObjectType(arr, objectTypeQuery, _parsedQueries.Keys.ToArray(),
            id => _metaQueryCache.GetOrAdd(id, BuildNCLMetaQuery), "Query");

        // XmlPorts — same shape, ObjectType=6, factory takes int xmlPortId.
        PopulateOneObjectType(arr, objectTypeXmlPort, _parsedXmlPorts.Keys.ToArray(),
            id => _metaXmlPortCache.GetOrAdd(id, BuildNCLMetaXmlPort), "XmlPort");

        // W-8b A-prime: now that every publisher table has an NCLMetaTable with its
        // tableTriggerEventHandler field populated, inject AL-emitted [NavEventSubscriber]
        // methods into each table's NavEventScope.registeredSubscriptions array. BC's own
        // CheckAndFireTriggerEventsAsync then dispatches them naturally during Insert/Modify/
        // Delete/Rename (no JmpHook on the dispatch path — that approach was killed by
        // R2R inlining in session 82e7fffc).
        // Read-only TryGetValue: only inject onto tables already built. A validate subscriber
        // on a not-yet-built table (e.g. an ISV on a precompiled BaseApp Purchase Header) is
        // injected LAZILY instead — see EventSubscriberPatches.InjectValidateSubsForTable, called
        // from BuildNCLMetaTable the moment that table's metatable is first built. Eagerly building
        // those publisher tables here perturbs unrelated setup (No.-Series assignment), so don't.
        AlRunnerV2.Patches.EventSubscriberPatches.InjectAll(
            id => _metaTableCache.TryGetValue(id, out var m) ? m : null);
    }

    public static NCLMetaTable NCLMetadata_GetMetaTableById(object self, int tableId, bool requireCompiled, int emitVersion)
    {
        var meta = EnsureTableInMetadataCache(tableId);
        if (meta != null)
            return meta;

        throw new InvalidOperationException(
            $"NCLMetadata.GetMetaTableById: no NCLMetaTable for table {tableId} (dependency source not parsed)");
    }

    public static object NCLMetadata_GetMetaApplicationObjectByType(
        object self, ObjectType objectType, int objectId, bool requireCompiled, int emitVersion)
    {
        if (objectType == ObjectType.Table)
        {
            var meta = EnsureTableInMetadataCache(objectId);
            if (meta != null)
                return meta;
        }

        throw new InvalidOperationException(
            $"NCLMetadata.GetMetaApplicationObject: no metadata for {objectType} {objectId}");
    }

    public static object NCLMetadata_GetMetaApplicationObjectById(
        object self, ApplicationObjectId objectId, bool requireCompiled, int emitVersion)
        => NCLMetadata_GetMetaApplicationObjectByType(
            self, objectId.ObjectType, objectId.ObjectNumber, requireCompiled, emitVersion);

    internal static NCLMetaTable? EnsureTableInMetadataCache(int tableId)
    {
        var meta = (NCLMetaTable?)_metaTableCache.GetOrAdd(tableId, BuildNCLMetaTable);
        if (meta == null)
            return null;

        var skeleton = BcRuntime.SkeletonNCLMetadata;
        if (skeleton == null)
            return meta;

        EnsureCachePopulatorReflection();
        if (_fNCLMetadataCacheEntries == null || _mCreateWithBase == null)
            return meta;

        var arr = _fNCLMetadataCacheEntries.GetValue(skeleton) as Array;
        const int objectTypeTable = 1;
        if (arr == null || arr.Length <= objectTypeTable)
            return meta;

        var slotDict = arr.GetValue(objectTypeTable);
        if (slotDict is not System.Collections.IDictionary dict || dict.Contains(tableId))
            return meta;

        if (_fNCLMetaAppObjMetadataLoaded != null)
            FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);
        var entry = _mCreateWithBase.Invoke(null, new object?[] { meta });
        if (entry != null)
            dict[tableId] = entry;
        return meta;
    }

    /// <summary>
    /// Insert one cache-entry per parsed object-id into
    /// metadataCacheEntries[objectTypeIndex]. Idempotent (TryAdd via dict[]= but skipped
    /// if the key already exists). Errors are logged + counted, never thrown.
    /// </summary>
    private static void PopulateOneObjectType(Array arr, int objectTypeIndex,
        int[] ids, Func<int, object?> buildMeta, string label)
    {
        if (_mCreateWithBase == null) return;
        if (arr.Length <= objectTypeIndex) return;
        var slotDict = arr.GetValue(objectTypeIndex);
        if (slotDict == null) return;
        var dict = (System.Collections.IDictionary)slotDict;

        int added = 0, failed = 0, skipped = 0;
        foreach (var id in ids)
        {
            if (dict.Contains(id)) { skipped++; continue; }
            object? meta;
            try { meta = buildMeta(id); }
            catch (Exception ex)
            {
                var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                Console.Error.WriteLine($"[NclMetadataCachePopulator] buildMeta({id}) threw {inner.GetType().Name}: {inner.Message}");
                meta = null;
            }
            if (meta == null) { failed++; continue; }

            // Mark metadataLoaded=true on the meta itself so the shared
            // NCLMetaApplicationObject.Populate path is skipped (belt-and-braces with
            // the §O JMP NoOp on Populate / CompileAndLoadClrObject).
            if (_fNCLMetaAppObjMetadataLoaded != null)
                FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);

            object? entry;
            try
            {
                entry = _mCreateWithBase.Invoke(null, new object?[] { meta });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RecordPatches] CacheEntry.CreateWithBase({label} {id}) failed: " +
                    ((ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex).Message));
                failed++;
                continue;
            }
            if (entry == null) { failed++; continue; }

            try { dict[id] = entry; added++; }
            catch { failed++; }
        }

        if (added > 0 || failed > 0 || skipped > 0)
            Console.Error.WriteLine($"[RecordPatches] PopulateNclMetadataCache[{label}]: added={added}, skipped={skipped}, failed={failed}, total={ids.Length}");
    }

    private static void EnsureCachePopulatorReflection()
    {
        if (_fNCLMetadataCacheEntries != null) return;

        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");
        if (nclAsm == null) return;

        var tNclMetadata = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadata");
        _fNCLMetadataCacheEntries = tNclMetadata?.GetField("metadataCacheEntries",
            BindingFlags.NonPublic | BindingFlags.Instance);

        _tNCLMetadataCacheEntry = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetadataCacheEntry");
        _mCreateWithBase = _tNCLMetadataCacheEntry?.GetMethod("CreateWithBase",
            BindingFlags.Public | BindingFlags.Static);

        var tNclMetaAppObj = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaApplicationObject");
        _fNCLMetaAppObjMetadataLoaded = tNclMetaAppObj?.GetField("metadataLoaded",
            BindingFlags.NonPublic | BindingFlags.Instance);
    }
}
