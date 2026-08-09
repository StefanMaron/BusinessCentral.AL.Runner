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
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Patches;

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
        // Pageextension ids are included alongside page ids: they used to share _parsedPages
        // and therefore used to get a slot here. #1710's split is about which object wins a
        // shared id, not about withdrawing the skeleton from ids only a pageextension claims.
        PopulateOneObjectType(arr, objectTypePage,
            _parsedPages.Keys.Concat(_parsedPageExtensions.Keys).Distinct().ToArray(),
            id => _metaFormCache.GetOrAdd(id, BuildNCLMetaForm), "Page");

        // Reports — §P, mirror via BuildNCLMetaReport using NCLMetaReport.CreateEmptyNCLMetaReport.
        //
        // NavGlobal.MetadataProvider (= SystemTenant.metadataProvider) is null until
        // BcRuntime.EnsureMetadataProviderSeeded() runs (skeleton built via
        // GetUninitializedObject; the real ctor that assigns it never ran). It was
        // previously seeded lazily, only on first virtual-Field-table (2000000041)
        // access — fine for FieldDataProvider, but any AL surface that reaches report
        // metadata through the INSTANCE path (Report.DefaultLayout / Report.WordXmlPart
        // -> NavGlobal.MetadataProvider.GetReportMetadata(id)) NREs on that null
        // receiver BEFORE our NCLMetadata_GetMetaApplicationObjectByType hook (and hence
        // RunnerMetaApplicationObjectLoader) is ever reached — a latent test-ordering
        // dependency (it "worked" in bundles where some other test happened to touch the
        // Field table first). Seed it here too: idempotent, and this method already runs
        // once per bundle before any AL test code executes, so every report-metadata AL
        // surface gets a real receiver regardless of test order.
        AlRunner.BcRuntime.EnsureMetadataProviderSeeded();
        // Every report the runner knows exists, not just the source-parsed ones — a report
        // in a precompiled dependency (Base Application 1306, say) reaches
        // NCLMetadata.GetMetaApplicationObject through exactly the same AL surfaces.
        PopulateOneObjectType(arr, objectTypeReport, KnownReportIds(),
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
        AlRunner.Patches.EventSubscriberPatches.InjectAll(
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
        else if (objectType == ObjectType.Report)
        {
            // Static Report.SaveAs/Run reach the engine via
            // NCLMetadata.GetMetaReportById → GetMetaApplicationObject(ObjectType.Report,…).
            // Serve the same skeleton NCLMetaReport the §P populator builds; the
            // Cecil-rewritten NCLMetaReport.CreateObjectInstance then constructs the
            // compiled Report{id} directly (real MetaReport comes from
            // AlReportMetadataRegistry in BeginInitialization).
            var meta = _metaReportCache.GetOrAdd(objectId, BuildNCLMetaReport);
            if (meta != null)
                return meta;
        }
        else if (objectType == ObjectType.Page)
        {
            // Reached from NavForm.GetMasterPage -> MetadataProvider.GetMasterPage when a
            // page is actually driven (the TestPage path). Serve the page's NCLMetaForm
            // with its REAL parsed control tree where the runner compiled the page itself,
            // and the skeleton otherwise — a skeleton is still better than "no such page"
            // for the lookup-only callers that were the only ones reaching here before.
            var meta = EnsureRealPageMetadata(objectId) ?? _metaFormCache.GetOrAdd(objectId, BuildNCLMetaForm);
            if (meta != null)
                return meta;
        }
        else if (objectType == ObjectType.Query)
        {
            // Precompiled / BaseApp-internal queries reach the engine via
            // NCLMetadata.GetMetaQueryById → GetMetaApplicationObject(ObjectType.Query, …),
            // NOT through NavQueryHandle.CreateTarget. Build the real NCLMetaQuery lazily
            // from SymbolReference.json (BuildRealNCLMetaQuery is cached). Returns null when
            // it can't be built (no symbols / unresolved table) → the original
            // NavNCLApplicationObjectNotFoundException surfaces rather than a fake.
            var meta = EnsureQueryInMetadataCache(objectId);
            if (meta != null)
                return meta;
        }
        else if (objectType == ObjectType.XmlPort)
        {
            // NavXmlPort.BeginInitialization reads get_NclMetaXmlPort, which resolves
            // through GetMetaApplicationObject(ObjectType.XmlPort, …). The §P populator
            // already builds an NCLMetaXmlPort for every parsed xmlport, but this
            // dispatch had no XmlPort branch, so EVERY instance-form xmlport
            // (XmlPort.Import/Export via a `XmlPort "Foo"` variable) fell through to the
            // not-found throw even though its metadata was sitting in the cache. Serve
            // the same cache the populator fills — identical shape to Report/Query above.
            //
            // Prefer a REAL metadata load: EnsureRealXmlPortMetadata parses the
            // emit-captured schema XML so BC's own XmlPort engine has a node tree to work
            // against. Without it the port resolves to a schema-less skeleton and every
            // real operation NREs (NCLMetaXmlPort.CreateObjectInstance,
            // NCLMetaApplicationObject.GetMetadataFromLoader). Falls back to the skeleton
            // for an xmlport we have no captured XML for — a precompiled dependency's.
            var meta = EnsureRealXmlPortMetadata(objectId)
                       ?? _metaXmlPortCache.GetOrAdd(objectId, BuildNCLMetaXmlPort);
            if (meta != null)
                return meta;
        }

        // BC's OWN exception type, not a plain InvalidOperationException. This is not
        // cosmetic: NCLMetadata.TryGetMetaApplicationObject is implemented as
        // `try { GetMetaApplicationObject(…) } catch (NavMetadataNotFoundException) { }
        //  catch (NavNCLArgumentException) { }`, so "the object is not there" is only
        // answerable as `false` when it arrives as one of those. Throwing a foreign type
        // turned every optional metadata probe into a hard failure — e.g.
        // MetadataProvider.ModifyReportRequestPage probes for the report-settings lookup
        // page (1560) with the Try form, which the runner does not always have, and the
        // whole request page failed to build instead of simply skipping that control.
        throw new NavMetadataNotFoundException(objectType, objectId);
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

    // Cache of lazily-built precompiled-query NCLMetaQuery objects (BuildRealNCLMetaQuery is
    // also cached, but this avoids re-resolving FindQueryType + the cache-entry insert).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, object?> _lazyMetaQueryByGetById = new();

    internal static object? EnsureQueryInMetadataCache(int queryId)
    {
        var meta = _lazyMetaQueryByGetById.GetOrAdd(queryId, id =>
        {
            var clrType = BcRuntime.FindQueryType(id);
            return clrType == null ? null : BuildRealNCLMetaQuery(id, clrType);
        });
        if (meta == null) return null;

        var skeleton = BcRuntime.SkeletonNCLMetadata;
        if (skeleton == null) return meta;

        EnsureCachePopulatorReflection();
        if (_fNCLMetadataCacheEntries == null || _mCreateWithBase == null) return meta;

        var arr = _fNCLMetadataCacheEntries.GetValue(skeleton) as Array;
        const int objectTypeQuery = 9;
        if (arr == null || arr.Length <= objectTypeQuery) return meta;
        if (arr.GetValue(objectTypeQuery) is not System.Collections.IDictionary dict || dict.Contains(queryId))
            return meta;

        if (_fNCLMetaAppObjMetadataLoaded != null)
            FieldPoke.SetInstance(_fNCLMetaAppObjMetadataLoaded, meta, true);
        try
        {
            var entry = _mCreateWithBase.Invoke(null, new object?[] { meta });
            if (entry != null) dict[queryId] = entry;
        }
        catch { /* cache insert is best-effort; meta is still returned */ }
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
