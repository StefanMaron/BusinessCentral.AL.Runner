// RecordPatches.cs — Attempt A prototype: NavRecord redirection to BC's own TempTableDataProvider.
//
// Strategy:
//   1. Parse AL source files → MetaField/MetaKey/MetaTable (public data classes in Types.dll).
//   2. Call NCLMetaTable.CreateFromMetaTable (internal) via reflection → real NCLMetaTable.
//   3. Hook NavRecordHandle.CreateTarget → construct Record{ID} with real NCLMetaTable.
//   4. Hook NavSession.DataAccessSource getter → return skeleton DataAccessSource.
//   5. Hook DataAccessSource.GetDataAccessForTable → call CreateTempDataAccess on self.
//   6. Hook NavDatabase.CollationAwareStringComparer → return OrdinalIgnoreCase comparer.
//
// This file is a SPIKE — not production code. Goal: get ≥1 test in 02-record-operations to PASS.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

/// <summary>Builds real NCLMetaTable objects from AL source, bypassing NCLMetadata service.</summary>
public static partial class RecordPatches
{
    // Reflected BC types — populated by Register().
    private static Type? _tMetaTable;
    private static Type? _tMetaField;
    private static Type? _tMetaKey;
    private static Type? _tFieldMetadataRelation;
    // MetaKey.sumIndexFields' element type (#3568) — the SIFT fields a key declares.
    private static Type? _tSumIndexField;
    private static Type? _tNavType;
    private static Type? _tFieldClass;
    // Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState — MetaField's obsoleteState ctor
    // param type (#1780). Bound in Register() alongside the other MetaField-adjacent types.
    private static Type? _tObsoleteState;
    // Microsoft.Dynamics.Nav.Types.Metadata.ALDataClassification — MetaField's
    // dataClassification ctor param type (#3545). Member 0 is CustomerContent, which is what
    // MetaField answers when nothing is passed.
    private static Type? _tALDataClassification;
    private static Type? _tMetaCalcFormula;
    private static Type? _tMetaFilter;
    private static Type? _tMetaCondition;
    private static Type? _tMetaFieldRelation;
    private static Type? _tFilterType;
    private static Type? _tNCLMetaTable;
    private static MethodInfo? _mCreateFromMetaTable;
    private static MethodInfo? _mCreateForTempTable;
    private static FieldInfo? _fVatdcInstance;
    private static Type? _tDataAccessSource;
    private static MethodInfo? _mCreateTempDataAccess;
    private static MethodInfo? _mGetVirtualDataAccess;
    private static System.Reflection.PropertyInfo? _pNclMetaTableIsVirtualTable;
    private static Type? _tGlobalFilters;
    private static Type? _tNavDatabase;
    private static Type? _tCollationAwareStringComparer;
    private static Type? _tSqlSortingProperties;
    private static FieldInfo? _fNavDatabaseCollation;
    private static FieldInfo? _fNavDatabaseSqlSortingProperties;
    private static object? _sqlSortingProperties;     // pre-built SqlSortingProperties
    private static FieldInfo? _fSessionDataAccessSource;
    private static FieldInfo? _fDasSession;
    private static FieldInfo? _fDasGlobalFilters;
    private static FieldInfo? _fDasTableVersionTokens;
    private static FieldInfo? _fDasSessionTransactionManager;
    private static object? _skeletonSessionTransactionManager;
    private static FieldInfo? _fNavRecordHandleTemp;
    private static object? _skeletonDatabase;   // pre-built NavDatabase skeleton

    // TempTableDataProvider fields for manual construction (bypass session.Database in ctor)
    private static FieldInfo? _fTtdpNavSession;
    private static FieldInfo? _fTtdpTable;
    private static FieldInfo? _fTtdpComparer;
    private static FieldInfo? _fTtdpPrimaryKeySortingFields;
    private static PropertyInfo? _pNclMetaKeySortingFieldsWithPK;  // NCLMetaKey.SortingFieldsWithPrimaryKeyFields (internal)
    private static object? _collationComparer;   // pre-built CollationAwareStringComparer

    // CalcNumeric hook — iterates in-memory rows via Filter() + accumulates count/sum/avg.
    private static MethodInfo? _mTtdpFilter;               // TempTableDataProvider.Filter(int,FiltersAndMarks,MutableRecordBuffer,SortingFieldList,bool)
    private static ConstructorInfo? _ctorFieldDictionaryNavValue; // FieldDictionary<NavValue>(Tuple<INavFieldMetadata,NavValue>[])

    // Cache: tableId → NCLMetaTable built from AL source.
    private static readonly ConcurrentDictionary<int, object?> _metaTableCache = new();

    // Cache: (DataAccessSource, tableId) → DataAccess (with TempTableDataProvider).
    // BC's real GetDataAccessForTable returns one shared TenantDataAccess for all Normal
    // tables; that DataAccess is constructed once in the DataAccessSource ctor. Our skeleton
    // routes everything to TempTableDataProvider, which is a per-table thing — but it must
    // still be **the same** TempTableDataProvider for every call on a given table, so that
    // Insert in one Record variable becomes visible to FindFirst in another. Without this
    // cache every Record-instance creates a fresh empty in-memory store.
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, object>> _dataAccessByTable = new();

    // ── Temporary-record DataAccess registry (issue #2524) ───────────────────────────────
    // A `Record X temporary` gets its OWN DataAccess from the isTemporary branch of
    // GetDataAccessForTableCore, and its store must contain EXACTLY the rows AL inserted --
    // nothing the runner puts there behind AL's back. That branch already honours the
    // invariant by construction: it skips every virtual-table populate below it.
    //
    // The runner's virtual-table populates, however, do not all run at DataAccess-creation
    // time. Three of them re-populate at FIND time, from DataAccess_IsManagedFindRequest /
    // DataAccess_AggregatePermissionSetGuardForGet, keyed only on the table id of the request
    // -- which a temporary record's request carries just the same. Those paths therefore wrote
    // real metadata rows into a temporary record's private store (measured on BC 28.1: a
    // `Record "Field" temporary` holding one AL row went from Count = 1 to Count = 178 across a
    // single FindSet, and FindSet returned the injected `timestamp` row, "No." = 0, instead of
    // AL's; `Record "Aggregate Permission Set" temporary` went 1 -> 123 and returned SECURITY;
    // `Record Date temporary` with a closed "Period Start" filter went 1 -> 31).
    //
    // Membership here is the signal the find-time paths lacked: it says "this DataAccess belongs
    // to a `temporary` record". It is registered in exactly one place, the isTemporary branch of
    // GetDataAccessForTableCore, which is the whole funnel -- Ncl's
    // DataAccessSource.GetDataAccessForTable is Cecil-REPLACED by
    // NavDataAccessSource_GetDataAccessForTable (NclCecilRewrite.Records.cs), so no Record
    // acquires a DataAccess by another route.
    //
    // Weak, so a temporary record's DataAccess stays collectable with the record; the value is
    // an unused sentinel, membership is the whole signal (same shape as
    // BlobStoreIsolationPatches._databaseBackedProviders).
    private static readonly ConditionalWeakTable<object, object> _temporaryRecordDataAccess = new();
    private static readonly object _temporaryRecordSentinel = new();

    /// <summary>
    /// Whether <paramref name="dataAccess"/> was handed out for a <c>temporary</c> record, whose
    /// store holds exactly what AL wrote to it. Every runner-side virtual-table populate must
    /// check this before writing rows: a temporary instance of a virtual table is an ordinary
    /// in-memory table that merely borrows that table's SHAPE, and the service tier's virtual
    /// provider never sees it. See issue #2524.
    /// </summary>
    internal static bool IsTemporaryRecordDataAccess(object? dataAccess)
        => dataAccess != null && _temporaryRecordDataAccess.TryGetValue(dataAccess, out _);

    // Source directories scanned for AL table definitions.
    private static readonly List<string> _sourceDirs = new();

    // Parsed table schemas: tableId → (fields, pkFieldIds).
    private static readonly Dictionary<int, ParsedTable> _parsedTables = new();

    // Parsed tableextension fields: base-table-name (lowercased) → list of extra fields.
    private static readonly Dictionary<string, List<ParsedField>> _parsedExtensionFields = new();

    // Parsed tableextension KEYS: base-table-name (lowercased) → the keys every tableextension
    // on that table declares, in merge order (#3216). The sibling of the line above, and it did
    // not exist: the fields half of a tableextension merge was written and the keys half never
    // was, so `RecordRef.KeyCount()`/`KeyIndex()` reported only the base table's own keys while
    // the extension's fields were already present as columns. Stored as NAMES — see
    // ParsedExtensionKey for why ids cannot be resolved at parse time.
    private static readonly Dictionary<string, List<ParsedExtensionKey>> _parsedExtensionKeys = new();

    // Parsed tableextension object ids: base-table-name (lowercased) → tableextension object
    // ids extending it, in AL declaration order (= the order BC registers them, which the
    // trigger pipeline preserves). Used to instantiate the emitted TableExtension{id} CLR
    // types and register them on each record (record-level triggers + field-validate handler
    // dispatch). See RecordPatches.CreateObjectInstance.cs / WireFieldTriggerHandlers.
    internal static readonly Dictionary<string, List<int>> _extensionIdsByBaseTable = new();

    /// <summary>
    /// #3600 — one entry per tableextension merged onto a base table: its declaring app id
    /// (null for a precompiled <c>.app</c>'s extension or an unresolvable <c>app.json</c> —
    /// reads as "does not match", the conservative direction) and whether it declares
    /// <c>modify(...)</c>. Feeds
    /// <see cref="RecordPatches.NclMetaTableFromBcDocument.ShouldBuildTableFromBcDocument"/>;
    /// see docs/object-metadata-from-bc.md#scope for what each case means. Written only from
    /// inside <see cref="MergeExtensionFields"/>, never at a call site.
    /// </summary>
    internal static readonly Dictionary<string, List<(Guid? OwningAppId, bool HasModify)>>
        _extensionSourceInfo = new();

    /// <summary>
    /// Merge <paramref name="fields"/>/<paramref name="keys"/> into
    /// <c>_parsedExtensionFields</c>/<c>_parsedExtensionKeys</c>, record
    /// <paramref name="extensionId"/> in <c>_extensionIdsByBaseTable</c> and this extension's
    /// app/modify signal in <see cref="_extensionSourceInfo"/> (#3600), and evict any
    /// already-built NCLMetaTable for the base table so the next lookup rebuilds it.
    ///
    /// The single writer both tableextension-field sources — the AL-source parser
    /// (<c>TryParseTableExtensionFile</c>) and the precompiled-.app symbol merge
    /// (<c>EnsureBcSymbolExtensionIndex</c>) — funnel through, so a future third writer
    /// inherits the eviction (#2126) and the source recording (#3600) automatically instead
    /// of needing to remember either. <paramref name="owningAppId"/>/<paramref name="hasModify"/>
    /// are recorded HERE rather than through a second call a writer could forget.
    /// </summary>
    private static void MergeExtensionFields(string baseTableName, int extensionId, IEnumerable<ParsedField> fields,
        IEnumerable<ParsedExtensionKey>? keys = null, Guid? owningAppId = null, bool hasModify = false)
    {
        if (string.IsNullOrEmpty(baseTableName)) return;
        var key = baseTableName.ToLowerInvariant();

        if (!_extensionSourceInfo.TryGetValue(key, out var sourceInfo))
            _extensionSourceInfo[key] = sourceInfo = new();
        sourceInfo.Add((owningAppId, hasModify));

        // De-dup by field id: the same extension can legitimately be scanned/merged more than
        // once (a dependency app's source dir registered both by its own suite AND by
        // sibling-source discovery, or a precompiled SymbolReference.json listing the same
        // field in both the base table's Tables[] entry and TableExtensions[].Fields — see
        // #1686 / #1711). A duplicated field id corrupts NCLMetaTable's positional field-count
        // arithmetic.
        if (!_parsedExtensionFields.TryGetValue(key, out var existing))
            _parsedExtensionFields[key] = new List<ParsedField>(fields);
        else
        {
            var existingIds = new HashSet<int>(existing.Select(f => f.FieldId));
            foreach (var f in fields)
                if (existingIds.Add(f.FieldId))
                    existing.Add(f);
        }

        // #3216 — the keys half of the same merge, de-duplicated on (key name + field-name
        // composition) for the same reason the fields above are de-duplicated on field id: one
        // extension can legitimately be scanned twice (a dependency source dir registered by
        // two suites, or a SymbolReference.json read again after an .app re-registration), and
        // a key listed twice would show up twice in RecordRef.KeyIndex(). The composition is
        // part of the identity because two DIFFERENT tableextensions on one table may each
        // declare a key called "Key1" — AL only requires a key name to be unique within its own
        // object — so name alone would silently drop the second one.
        if (keys != null)
        {
            if (!_parsedExtensionKeys.TryGetValue(key, out var existingKeys))
                _parsedExtensionKeys[key] = existingKeys = new List<ParsedExtensionKey>();
            foreach (var k in keys)
            {
                if (k.FieldNames.Count == 0) continue;
                if (existingKeys.Any(e => string.Equals(e.Name, k.Name, StringComparison.OrdinalIgnoreCase)
                        && e.FieldNames.Count == k.FieldNames.Count
                        && e.FieldNames.Zip(k.FieldNames, (a, b) =>
                               string.Equals(a, b, StringComparison.OrdinalIgnoreCase)).All(x => x)))
                    continue;
                existingKeys.Add(k);
            }
        }

        if (extensionId > 0)
        {
            if (!_extensionIdsByBaseTable.TryGetValue(key, out var extIds))
                _extensionIdsByBaseTable[key] = extIds = new List<int>();
            if (!extIds.Contains(extensionId))
                extIds.Add(extensionId);
        }

        EvictCachedMetaTableForBaseTable(baseTableName);
    }

    /// <summary>
    /// A parsed table's own fields PLUS every field any tableextension has merged onto it
    /// (<see cref="_parsedExtensionFields"/>, keyed by table name — the extension may be
    /// AL-source-parsed in this bundle or a dependency's, or precompiled in a dependency
    /// .app; <see cref="MergeExtensionFields"/> is the single writer for both). De-duplicated
    /// by field id the same way <see cref="RecordPatches.NclMetaTableBuilder"/> does when it
    /// builds the runtime NCLMetaTable, so a control-binding lookup and the record's own
    /// field layout never disagree about which fields a table has (issue #2490: a TestPage
    /// control bound to an extension field threw <c>testpage-control-binding</c> because
    /// <see cref="GetPageControlFieldMap"/> and <see cref="TryResolveDependencyFieldId"/> each
    /// searched only <c>ParsedTable.Fields</c> — the base table's own declared fields — and
    /// never looked at <c>_parsedExtensionFields</c> at all, even though the record itself
    /// already carries the extension's fields via this same dictionary).
    /// </summary>
    internal static IEnumerable<ParsedField> GetAllFieldsIncludingExtensions(ParsedTable table)
    {
        if (!_parsedExtensionFields.TryGetValue(table.TableName.ToLowerInvariant(), out var extFields)
            || extFields.Count == 0)
            return table.Fields;

        var baseFieldIds = new HashSet<int>(table.Fields.Select(f => f.FieldId));
        var extFieldsNew = extFields.Where(f => !baseFieldIds.Contains(f.FieldId));
        return table.Fields.Concat(extFieldsNew);
    }

    // Set to true once Register() has been called.
    private static bool _registered;

    /// <summary>
    /// Drop all per-bundle parsed/built table &amp; sub-object metadata, the record
    /// CLR-type cache, the registered source dirs, and the in-memory row store so
    /// the SAME process can re-load an edited bundle of the same identity (server
    /// mode). Reflection handles and the installed hooks (<c>_registered</c>) are
    /// preserved. Re-run <see cref="AddSourceDir"/> + emit + SetTestAssembly after.
    /// See <see cref="BcRuntime.ResetForNewBundleReload"/> for the full reload
    /// contract and the field-schema-edit limitation.
    /// </summary>
    public static void ResetForReload()
    {
        _metaTableCache.Clear();
        // #3552 — the ledger names LIVE NCLMetaTable instances as already carrying BC's
        // document, so it is meaningless the moment the line above drops them, and leaving it
        // populated would make the next cycle's tables keep the derivation in silence.
        ClearBcDocumentBackedTables();
        // #3607, the same statement for reports: the memo holds properties parsed out of the
        // PREVIOUS bundle's metadata documents, and a --watch cycle that edits a report's
        // ProcessingOnly would otherwise keep answering the old value with nothing to show
        // for it. Report ids repeat across reloads, so a stale entry is a wrong answer rather
        // than a miss.
        ClearBcReportDocuments();
        // #3604, the same statement one object kind over: the memo holds the control tree
        // parsed out of the PREVIOUS bundle's page documents, and a --watch cycle that adds
        // or hides a control would otherwise keep answering the old tree. Page ids repeat
        // across reloads, so a stale entry is a wrong answer rather than a miss.
        ClearBcPageControlDocuments();
        // #3605: and the pageextension deltas that merge into those documents, which are keyed
        // by the EXTENSION's id — a separate id space, so a separate memo to drop.
        ClearBcPageExtensionControls();
        // #3653: and the per-field Editable those rows resolve against. It is keyed by
        // (table, field) and read through the rebuilt NCLMetaTable, so a stale entry outlives
        // the very table it describes.
        ClearBcMetaFieldEditable();
        // #3121: every table is rebuilt from scratch below, so carrying the previous bundle's
        // pending CalcFormula rebuilds forward only buys a wasted repopulate pass on the next
        // .app registration.
        ClearUnresolvedCalcFormulaTables();
        // #3263, the sibling of the line above: a note saying THIS bundle's CalcFormula named
        // something unresolvable must not refuse the next bundle's FlowField.
        ClearUnresolvedCalcFormulaReferences();
        // #3306, the same statement one property over: a note saying THIS bundle's
        // TableRelation named something unresolvable must not refuse the next bundle's field.
        ClearUnresolvedRelationReferences();
        _recordTypeCache.Clear();
        // Sibling of the line above, and it said so in its own comment ("Cached HITS only,
        // mirroring _recordTypeCache") while not being mirrored HERE — nothing cleared it on
        // any path. Both map an AL object id to a CLR type resolved out of the emitted test
        // assembly, so both name a generation of types that this reload is replacing: from
        // the second --server request / --watch cycle onward, FindTableExtensionType(extId)
        // handed back the PREVIOUS bundle's TableExtension{id} while every record type around
        // it came from the new one. That is #1683's two-live-modules-for-one-AL-identity
        // shape arriving through a cache instead of through a loader, and it is silent: the
        // stale extension binds to the new record and its fields read the wrong storage.
        // Safe, for the same reason _recordTypeCache's clear is: hits only, and a miss falls
        // through to the assembly scan that repopulates it.
        _tableExtensionTypeCache.Clear();
        _parsedTables.Clear();
        _parsedExtensionFields.Clear();
        // #3216 — cleared alongside _parsedExtensionFields, never separately: the two halves
        // describe one merge, and a keys map surviving a reload would attach the previous
        // bundle's extension keys to the next bundle's tables.
        _parsedExtensionKeys.Clear();
        _extensionIdsByBaseTable.Clear();
        // #3600 — cleared alongside the two above for the same reason: a stale entry here
        // would attribute the PREVIOUS bundle's app boundaries to the next one's tables.
        _extensionSourceInfo.Clear();
        // #2478: must invalidate _bcSymbolTableIndex too, not just _bcSymbolExtensionIndexBuilt —
        // EnsureBcSymbolExtensionIndex's only call site is inside EnsureBcSymbolTableIndex, gated
        // by `_bcSymbolTableIndex != null`. Leaving that index populated made the flag reset above
        // a no-op forever: on request 2 of a warm --server/--watch process, EnsureBcSymbolTableIndex
        // short-circuited before ever reaching the extension merge again, so precompiled
        // tableextension fields silently vanished from every metatable from the second request on.
        // Shares InvalidateBcAppIndexes with AddBcAppPath (RecordPatches.BcAppFallback.cs) so the
        // two call sites can't drift apart again the way they did here.
        lock (_bcTableIndexLock)
        {
            // #2755: the REGISTERED set goes too, not just the indexes derived from it.
            // InvalidateBcAppIndexes drops the derived table/extension indexes so the next lookup
            // rebuilds them FROM _bcAppPaths — so leaving that list populated meant bundle 2 in a
            // --server/--watch process rebuilt against its own registrations UNION every earlier
            // bundle's, while a fresh single-bundle process running bundle 2 alone saw only its
            // own. The neighbouring per-bundle state already held this invariant
            // (InstallTriggerRunner.ResetForNewBundle clears _depAssemblies), and the server
            // path's own comment states the intent: "New bundle in the server session: replace
            // (not inherit) the install-trigger registrations".
            //
            // Safe for the PER-BUNDLE registrations because every caller re-registers
            // immediately afterwards, and registers the FULL resolved closure rather than a
            // delta — platform and Base Application .apps included. ResetForNewBundleReload
            // (BcRuntime.cs, the single caller of this method) runs at Program.cs 2196 on the
            // CLI path and 4049 on the server path; the matching registrations are at 2354/2357
            // and 4533/4534, both AFTER. A clear therefore removes nothing the current bundle
            // does not immediately put back.
            //
            // NOT safe for all of them, which is why this is ClearPerBundleBcAppPaths and not
            // _bcAppPaths.Clear(): the SystemApp package is registered once per PROCESS, by
            // RegisterSystemAppPackage() from Register(), and no per-bundle path re-adds it. A
            // flat clear unregistered the AL source for every NCL-internal system table for the
            // rest of a --server/--watch process — and since this method also clears
            // _parsedTables and _metaTableCache, that registration is the only thing they can
            // be rebuilt from. Two AlRunner.Tests classes caught it as
            // "no NCLMetaTable for table N (dependency source not parsed)".
            //
            // #2478 is the reason this is spelled out rather than done quietly: the last defect
            // in this same reset path was an index reset that did not reset ENOUGH, and it made
            // precompiled tableextension fields vanish from every metatable from the second
            // server request on. That failure was silent; this one was too.
            //
            // #2939: the sibling list _bcQuerySymbolJsonPaths goes with it, and now does. It
            // feeds _bcSymbolQueryIndex through the same derived/registered split and is the
            // other input to RegisteredBcAppSymbolStateKey. #2755 left it alone deliberately,
            // having scoped itself to _bcAppPaths; measured, it was the worse of the two,
            // because its merge is first-wins and the stale file sorts first — bundle 2 got
            // bundle 1's query column ids INSTEAD of its own rather than in addition to them.
            // Its clear is unconditional (no SystemApp-style exemption applies: every entry is
            // a loose per-bundle SymbolReference.json), and lives in ClearPerBundleBcAppPaths
            // beside the one above so the two cannot drift apart the way #2478's pair did.
            ClearPerBundleBcAppPaths();
        }
        // #3207: the object-reference const memo goes with them, and must be cleared HERE rather
        // than left to expire — it has no expiry. ResolveObjectIdByKindAndName memoises
        // (kind, name) -> id on top of ResolveTableIdByName / EnumerateKnownAlObjects, which read
        // _parsedTables and _bcSymbolTableIndex, i.e. exactly the state the three statements above
        // just discarded. Its doc comment justifies caching successes with "an id never changes
        // once known", and that holds INSIDE a bundle and not across a reload: bundle 2 of a
        // --server/--watch process declaring the same object name at a different id kept getting
        // bundle 1's, so the memo defeated an invalidation its own dependency performs. The
        // consequence is silent — a where() condition pinned to the wrong table id computes a
        // plausible wrong number rather than failing — which is the failure #3205's own comment
        // says it is avoiding, and the same shape as #2478 and #2755 in this same reset path.
        _objectRefConstIds.Clear();
        _fieldTriggersWiredTables.Clear();
        _parsedPages.Clear();
        _parsedPageExtensions.Clear();
        _parsedReports.Clear();
        _parsedReportExtensions.Clear();
        _parsedQueries.Clear();
        _parsedXmlPorts.Clear();
        _parsedObjectDecls.Clear();
        _parsedObjectDeclOwners.Clear();
        _parsedObjectCaptions.Clear();
        // Both keyed by (AppId, Name), both populated by the same per-file sweep
        // (ParseSourceFileIntoAllExtractors) as every dict above — an edited re-run that
        // renames or removes a profile/permission set must not keep serving the stale
        // declaration, the same reason _parsedTables/_parsedPages/_parsedObjectDecls are
        // cleared here. _parsedProfiles was missing this before #2357 — the same "current
        // bundle source" gap that left permission sets unattributed also left profiles
        // able to go stale across a --server reload.
        _parsedProfiles.Clear();
        _parsedPermissionSets.Clear();
        // #3226: and the directory -> owning-app memo the two lines above re-read THROUGH.
        // ResolveOwningApp answers "the nearest app.json at or above this directory" once per
        // directory and keeps it for the life of the process, so a --watch cycle that edits
        // app.json's name or id re-parsed every profile, permission set and table-metadata
        // source and attributed all of them to the PREVIOUS identity — the clear above without
        // this one only re-reads the declarations, not who owns them. Cost: one app.json
        // walk-up per source directory per cycle (measured in #3226's PR body).
        _owningAppByDir.Clear();
        _metaFormCache.Clear();
        // #1957: the "already (successfully|un-)loaded" bookkeeping is a statement about
        // the NCLMetaForm instances _metaFormCache.Clear() just discarded — it must go
        // with them, or the next lookup short-circuits a brand-new skeleton as
        // "already loaded" and silently serves a control-less page. See
        // ResetPageMetadataForReload's doc comment for the full reasoning.
        ResetPageMetadataForReload();
        _metaReportCache.Clear();
        _metaQueryCache.Clear();
        // #3210: the two id-keyed caches of BUILT NCLMetaQuery objects that sit on top of
        // _metaQueryCache and were the only pieces of this chain nothing dropped.
        // BuildRealNCLMetaQuery keys on the query id ALONE while taking the emitted CLR type
        // as an argument, so bundle 2's query 50100 was answered with bundle 1's NCLMetaQuery
        // built against bundle 1's CLR type; EnsureQueryInMetadataCache sits on top of it and
        // additionally memoizes BcRuntime.FindQueryType(id), a per-bundle answer that
        // ResetForNewBundleReload clears (_queryTypeCache) immediately before calling this.
        // Both memoize the NEGATIVE answer too, which is the half that bites without a second
        // bundle even declaring the query differently: an id asked about while it was
        // unresolvable stayed null for the rest of the process.
        _realMetaQueryCache.Clear();
        _lazyMetaQueryByGetById.Clear();
        // Same shape again, one object type over: id -> built NCLMetaPermissionSet, derived
        // from EnumerateKnownPermissionSets() -> _parsedPermissionSets, which this method
        // clears a few lines above. Nothing cleared it, so a permission set an edited bundle
        // renames or redeclares kept answering with the previous bundle's metadata, and one
        // that bundle 1 did not declare kept answering "no such permission set" (the null is
        // cached) even after bundle 2 declared it.
        ResetPermissionSetMetadataForReload();
        _metaXmlPortCache.Clear();
        // #3172: the xmlport side of #1957, never mirrored. Both sets are statements about
        // the specific NCLMetaXmlPort instances _metaXmlPortCache.Clear() has just discarded
        // — see ResetXmlPortMetadataForReload's doc comment, and ResetPageMetadataForReload's
        // for the page-side failure this prevents.
        ResetXmlPortMetadataForReload();
        _sourceDirs.Clear();
        _installBaseline = null;
        SetActiveDepCompanyBaseline(null);
        _isolatedStorageBaseline = null;
        _autoIncrementBaseline = null;
        // Drop the in-memory table rows so an edited re-run starts clean instead of
        // seeing Inserts from the previous run (which would e.g. throw "already exists").
        _dataAccessByTable.Clear();
        // _materialisationGates is deliberately NOT cleared here, and no reset path clears it.
        // A gate's latch names the storage INSTANCE it was set for, so clearing the map above
        // invalidates it on its own — see RecordPatches.TableMaterialisation.cs. Clearing the
        // gates as well would be a second mechanism for one fact, which is what let the fast
        // path trust a latch that no longer described anything (the reset paths that drop
        // storage are not all in one place, and only one of them knew to do it); worse, it
        // hands out fresh gate objects, so a reset racing a materialisation would put two
        // threads inside the create -> hydrate step under two different monitors. The gates are
        // a handful of empty objects keyed weakly by DataAccessSource, and they die with it.
        // Registered table connections cache one CrmTestDataProvider per table id, bound to
        // the previous run's NCLMetaTable — they go with the rows (#2725).
        TableConnectionPatches.ResetForReload();
        // #3101 review: the Object Metadata row PROVENANCE goes with the rows too. The flag
        // means "a store for 2000000071 was found already holding rows" — a fact about the store
        // _dataAccessByTable.Clear() above just dropped. It is one-way, so nothing else can put
        // it back, and while it is stale NoSourceRefusalIsActiveFor(2000000071) answers false and
        // the nine payload columns read BLANK instead of refusing: #2771's original silent
        // default, in the PR that closes #2771. Third time this reset path has outlived state it
        // owned (#2478 and #2755 are commented above; #3184 is the same shape one file over).
        // Safe because the next bundle re-derives it. GetDataAccessForTableCore routes EVERY
        // access to 2000000071 through MaterialiseObjectMetadataStore, which runs the populate on
        // every one of them rather than only at creation, and RunObjectMetadataPopulateOnce is
        // keyed on the PROVIDER — which a reload replaces. So the first touch after this clear
        // re-runs the populate and re-answers the question, and no read of a payload column can
        // reach the guard before it has.
        ResetObjectMetadataRowProvenance();
    }

    public static void AddSourceDir(string dir) => AddSourceDirs(new[] { dir });

    /// <summary>
    /// Runs all eight source extractors (table, tableextension, page, report, query,
    /// xmlport, object-decl, object-caption) over ONE already-read file's text (#1903).
    /// <para>
    /// Before this, <see cref="AddSourceDirs"/>' per-file loop called all eight directly —
    /// each extractor is a thin foreach over <c>ParseAlObjects(text)</c>, which built its
    /// OWN full AL syntax tree from the same text. A source tree of N files therefore cost
    /// 8N parses of eight IDENTICAL trees, measured on a 7,339-file real-world corpus as
    /// ~59,000 parses / 29.7s per pass instead of 7,339 parses. <see cref="ParseAlObjects"/>
    /// now memoizes its most-recently-built tree keyed on (text, active preprocessor
    /// symbols) — see the comment there — so calling the eight extractors back-to-back on
    /// the SAME text, as both callers below do, costs one real parse plus seven cache hits.
    /// </para>
    /// <para>
    /// The (text, symbols) key is deliberate, not an oversight: #1900 was caused by a
    /// parser that stopped seeing <c>--define</c> symbols because a field FROZE at
    /// type-init before <c>BcCompiler.SetExtraPreprocessorSymbols</c> ran. A cache keyed on
    /// text alone would reintroduce that bug silently — two calls for the same text under
    /// different <c>--define</c> sets are a genuinely different parse, not a cache hit.
    /// </para>
    /// </summary>
    private static void ParseSourceFileIntoAllExtractors(string text, string? filePath = null)
    {
        // Table and tableextension need the file PATH too, same reason as the profile/
        // permission-set calls below: #3600's table-metadata-source guard has to tell a
        // same-app tableextension (BC folds its fields/keys into the base table's own
        // document) from a cross-app one (it cannot), and "same app" is only knowable from
        // the app.json that owns each file.
        TryParseTableFile(text, filePath);
        TryParseTableExtensionFile(text, filePath);
        TryParsePageFile(text);
        TryParseReportFile(text);
        TryParseQueryFile(text);
        TryParseXmlPortFile(text);
        TryParseObjectDeclFile(text, filePath);
        TryParseObjectCaptionFile(text);
        // Profiles need the file PATH, not just its text: a profile has no object id, and
        // its "All Profile" row carries the declaring app's id and name, which are only
        // knowable from the app.json that owns the file (#2317).
        TryParseProfileFile(text, filePath);
        // Permission sets need the file PATH for the same reason profiles do — their
        // "Metadata Permission Set" row carries the declaring app's id, only knowable from
        // the app.json that owns the file (#2357).
        TryParsePermissionSetFile(text, filePath);
    }

    /// <summary>
    /// The Register()-time equivalent of <see cref="AddSourceDirs"/>' per-file loop: one
    /// pass over every registered source dir, reading each file's text exactly once and
    /// running all eight extractors on it via <see cref="ParseSourceFileIntoAllExtractors"/>
    /// (#1903). This replaced seven independent sweeps (one per extractor kind), each of
    /// which re-walked every source dir and re-read every file from disk on its own — 7
    /// directory walks + 7 file reads + (with the old un-memoized parser) 8 tree builds per
    /// file, instead of 1 of each.
    /// </summary>
    private static void ParseAllRegisteredSourceFiles()
    {
        foreach (var dir in _sourceDirs)
            foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al"))
                ParseSourceFileIntoAllExtractors(File.ReadAllText(file), file);
    }

    /// <summary>
    /// Register N source dirs and populate the NCLMetadata cache ONCE for the whole
    /// batch, instead of once per dir (#1833). <see cref="AddSourceDir"/> delegates
    /// here with a single-element array so its per-call-populate semantics are
    /// unchanged for callers that add one dir at a time outside a loop (e.g. the
    /// sibling-dependency emit loop in Program.cs, which calls AddSourceDir for one
    /// dir at a time interleaved with other per-dep work and needs each dir's tables
    /// visible before the next dep's symbols.json is written).
    /// <para>
    /// <see cref="PopulateNclMetadataCache"/>'s own cost is driven by the TOTAL number
    /// of ids known so far (it rebuilds <c>_parsedTables.Keys.ToArray()</c> etc. and
    /// walks the whole set with an idempotent skip-if-cached check) — not by what a
    /// single dir contributed. Calling it once per dir in a loop of N dirs is
    /// therefore O(N) calls each doing O(total-ids-so-far) work: quadratic in N. This
    /// entry point parses every dir first, THEN calls it exactly once over the
    /// complete set — same total ids processed, but the "once per dir" work is
    /// eliminated (N calls -&gt; 1 call). Every AL source dir is still parsed exactly
    /// once (per the existing <see cref="_sourceDirs"/> de-dup below) and the cache is
    /// still guaranteed fully populated before this method returns, so any caller
    /// that reads the cache immediately afterward (as the register-source-dirs stage's
    /// caller does, before build-app-groups/emit/compile ever runs) sees every dir's
    /// metadata — new dirs are never silently dropped from the merge.
    /// </para>
    /// </summary>
    public static void AddSourceDirs(IEnumerable<string> dirs)
    {
        var parsedAny = false;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            // De-dup: BuildSiblingSourceDeps (Program.cs) can legitimately call this for the
            // SAME dependency source dir twice — once while matching declared deps to sibling
            // source apps, once while emitting the synthetic workspace .app for a dep that
            // needs a fresh build. Without this guard the same dir lands twice in _sourceDirs,
            // so ParseAllRegisteredSourceFiles() parses its .al files twice on the next
            // Register()/rebuild. For a dependency that declares a `tableextension` on a
            // table whose base metadata comes from elsewhere (e.g. a platform-app table),
            // that duplicated every extension field id in _parsedExtensionFields — see #1686.
            // The dedup here is defense in depth alongside the field-id dedup in
            // TryParseTableExtensionFile.
            if (_sourceDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;
            _sourceDirs.Add(dir);
            // If Register() already ran (it runs before the bucket loop), parse immediately.
            // The NCLMetadata cache is populated once below, after every dir in this batch
            // has been parsed — see the batching rationale on the doc comment above. Every
            // .al file's text is read ONCE and handed to all eight extractors together
            // (#1903) — see ParseSourceFileIntoAllExtractors.
            if (_registered)
            {
                var _diagBefore = ParseObjectTextCallCount;
                var _diagHitsBefore = ParseTreeCacheHitCount;
                var _diagFiles = 0;
                foreach (var file in AlRunner.Infrastructure.SafeDirectoryScan.Files(dir, "*.al"))
                {
                    _diagFiles++;
                    ParseSourceFileIntoAllExtractors(File.ReadAllText(file), file);
                }
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PARSE_COUNTS") == "1")
                    Console.Error.WriteLine(
                        $"[parse-counts] dir files={_diagFiles} realParses={ParseObjectTextCallCount - _diagBefore} " +
                        $"treeCacheHits={ParseTreeCacheHitCount - _diagHitsBefore}");
                parsedAny = true;
            }
        }
        if (parsedAny)
            PopulateNclMetadataCache();
    }

    /// <summary>
    /// Reflect on the BC assemblies and build NCLMetaTable objects from any AL sources added so far.
    /// Must be called after ForceLoadBcDlls() but before any test runs.
    /// </summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;

        var typesAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
        var nclAsm = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Ncl");

        // Data types (Microsoft.Dynamics.Nav.Types)
        _tMetaTable = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaTable")!;
        _tMetaField = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaField")!;
        _tMetaKey   = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaKey")!;
        _tFieldMetadataRelation = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FieldMetadataRelation")!;
        _tSumIndexField = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.SumIndexField")!;
        _tNavType   = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.NavType")!;
        _tFieldClass = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FieldClass")!;
        _tObsoleteState = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.ObsoleteState")!;
        _tALDataClassification = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.ALDataClassification")!;
        _tMetaCalcFormula = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaCalcFormula")!;
        _tMetaFilter  = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaFilter")!;
        _tMetaCondition = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaCondition")!;
        _tMetaFieldRelation = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaFieldRelation")!;
        _tFilterType  = typesAsm.GetType("Microsoft.Dynamics.Nav.Types.Metadata.FilterType")!;

        // NCLMetaTable and factory (Microsoft.Dynamics.Nav.Runtime / Ncl)
        _tNCLMetaTable = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable")!;
        _mCreateFromMetaTable = BcShape.Method(
            _tNCLMetaTable, "CreateFromMetaTable", BindingFlags.NonPublic | BindingFlags.Static,
            "AL record data access");

        // NCLMetadata.GetMetaTableById(int,bool,int), NCLMetadata.GetMetaApplicationObject
        // (ObjectType,int,bool,int and ApplicationObjectId,bool,int), and
        // NCLMetaTable.GetFieldByNo(extensionId,fieldNo) are all Cecil-owned (see
        // NclCecilRewrite.cs).

        // DataAccessTableVersionTokens.CreateForTempTable()
        var tDatv = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccessTableVersionTokens")!;
        _mCreateForTempTable = BcShape.Method(
            tDatv, "CreateForTempTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            "AL record data access");

        // VirtualAndTempTransactionalDataCache.Instance
        var tVatdc = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.VirtualAndTempTransactionalDataCache")!;
        _fVatdcInstance = BcShape.Field(
            tVatdc, "Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            "AL record data access");

        // DataAccessSource and CreateTempDataAccess
        _tDataAccessSource = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.DataAccessSource")!;
        _mCreateTempDataAccess = BcShape.Method(
            _tDataAccessSource, "CreateTempDataAccess", BindingFlags.NonPublic | BindingFlags.Instance,
            "AL record data access");
        // GetVirtualDataAccess(NCLMetaTable) — BC's real router for virtual/system tables
        // (Field=2000000041 → FieldDataProvider, AllObj=2000000038 → AllObjDataProvider, …).
        // We re-route virtual tables here instead of dumping them into the empty temp store.
        _mGetVirtualDataAccess = _tDataAccessSource.GetMethod("GetVirtualDataAccess",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _pNclMetaTableIsVirtualTable = nclAsm
            .GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaTable")!
            .GetProperty("IsVirtualTable", BindingFlags.Public | BindingFlags.Instance);

        // GlobalFilters (public ctor)
        _tGlobalFilters = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.GlobalFilters")!;

        // NavSession fields for DataAccessSource
        var tNavSession = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession")!;
        _fSessionDataAccessSource = tNavSession.GetField("<DataAccessSource>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // NavDatabase — skeleton instance (returned by NavSession.Database hook)
        _tNavDatabase = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavDatabase")!;
        _tCollationAwareStringComparer = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.CollationAwareStringComparer");
        _tSqlSortingProperties = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.SqlSortingProperties");
        _fNavDatabaseCollation = _tNavDatabase.GetField("collationAwareStringComparer",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fNavDatabaseSqlSortingProperties = _tNavDatabase.GetField("sqlSortingProperties",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Pre-build SqlSortingProperties so it's available for both the skeleton DB and the
        // NavSession.SortingProperties hook (used by RecordBufferComparer in TempTableDataProvider).
        _sqlSortingProperties = BuildSqlSortingProperties();

        // Build skeleton NavDatabase once — NavDatabase.CollationAwareStringComparer is JMP-hooked
        // so any non-null NavDatabase is sufficient; we just need it to not NRE.
        _skeletonDatabase = RuntimeHelpers.GetUninitializedObject(_tNavDatabase);
        if (_fNavDatabaseCollation != null && _tCollationAwareStringComparer != null)
        {
            var comparer = BuildCollationAwareComparer();
            if (comparer != null) _fNavDatabaseCollation.SetValue(_skeletonDatabase, comparer);
        }
        if (_fNavDatabaseSqlSortingProperties != null && _sqlSortingProperties != null)
            _fNavDatabaseSqlSortingProperties.SetValue(_skeletonDatabase, _sqlSortingProperties);

        // Populate sqlDatabaseProperties so NavDatabase.SqlDatabaseProperties returns a
        // non-null object. BaseApp telemetry (FeatureTelemetry.LogUsage → ALGetModuleInfo)
        // reads NavGlobal.AppDatabase.SqlDatabaseProperties.ApplicationFamily; on a skeleton
        // with no SQL there is no real ApplicationFamily, and the field defaults to "" —
        // a faithful empty-family value (telemetry-only, dropped as HTTP egress is OOS).
        var fSqlDbProps = _tNavDatabase.GetField("sqlDatabaseProperties",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var tSqlDbProps = _tNavDatabase.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.NavSqlDatabaseProperties");
        if (fSqlDbProps != null && tSqlDbProps != null)
        {
            // GetUninitializedObject skips field initializers, so applicationFamily and lockObj
            // are null and databasePropertiesReady is false. The ApplicationFamily getter calls
            // ReadDatabaseProperties(), which would Monitor.TryEnter(lockObj) (null → ArgNull) and
            // open a NavSqlConnectionScope (no SQL on the skeleton). Set databasePropertiesReady=true
            // and applicationFamily="" so the getter short-circuits to the empty family value —
            // the faithful "no SQL family known" result; telemetry is dropped (HTTP egress OOS).
            var sqlDbProps = RuntimeHelpers.GetUninitializedObject(tSqlDbProps);
            tSqlDbProps.GetField("applicationFamily", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, string.Empty);
            tSqlDbProps.GetField("lockObj", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, new object());
            tSqlDbProps.GetField("databasePropertiesReady", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, true);
            // #2300: NavSqlDatabaseProperties.InvalidIdentifierChars is read by
            // NavSqlStatementHelper.ConvertToSqlIdentifier (via NCLMetaTable.SqlTableName),
            // which a Query with a FlowField column reaches while naming the FlowField's
            // synthesized sub-dataitem (NCLMetaQuery.CreateSubQueryForFlowFieldCalculation
            // → SqlTableDataProviderHelper.CreateDataItemFromFlowField). GetUninitializedObject
            // leaves the private `invalidIdentifierChars` field null, and ConvertToSqlIdentifier
            // iterates it unconditionally — NRE before any row is read, regardless of whether the
            // identifier it's naming actually contains an invalid character. Populate it from BC's
            // own internal constant (read via reflection, not restated as a literal, so a future
            // BC version's different default is picked up automatically rather than silently
            // diverging) — the same value the real ctor assigns before any SQL round-trip.
            var fDefaultInvalidChars = tSqlDbProps.GetField("DefaultInvalidIdentifierChars",
                BindingFlags.NonPublic | BindingFlags.Static);
            var defaultInvalidChars = fDefaultInvalidChars?.GetRawConstantValue() as string ?? ".\"\\/'%][";
            tSqlDbProps.GetField("invalidIdentifierChars", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.SetValue(sqlDbProps, defaultInvalidChars);
            fSqlDbProps.SetValue(_skeletonDatabase, sqlDbProps);
        }
        // companyTokens — BC's own NavDatabase ctor does `companyTokens = new CompanyTokens(this)`,
        // and GetUninitializedObject skips it. Anything that maps a company token back to a name
        // (NavMedia.MediaExists → Database.CompanyTokens.Get(ParentCompanyToken), and every other
        // company-scoped platform-table read) then NREs on the null. Build the real type through
        // its real constructor so its own field initializers run: companyNames starts as
        // { string.Empty }, i.e. token 0 == the runner's single unnamed company, which is exactly
        // what the rest of the runner uses (RecordImplementation.GetActiveCompany returns "").
        var fCompanyTokens = _tNavDatabase.GetField("companyTokens",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var tCompanyTokens = _tNavDatabase.Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.CompanyTokens");
        if (fCompanyTokens != null && tCompanyTokens != null)
        {
            var ctorCompanyTokens = tCompanyTokens.GetConstructor(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, new[] { _tNavDatabase }, modifiers: null);
            if (ctorCompanyTokens != null)
                AlRunner.Infrastructure.FieldPoke.SetInstance(fCompanyTokens, _skeletonDatabase,
                    ctorCompanyTokens.Invoke(new[] { _skeletonDatabase }));
        }

        // tenant — BC's own NavDatabase ctor takes the owning NavTenant and stores it in this
        // private field; GetUninitializedObject skips it, so NavDatabase.Tenant was null on the
        // skeleton even though NavSession.Tenant was not. Both NavSession.get_Database and
        // NavTenant.get_Database are Cecil-rewritten to hand out this one skeleton instance, so
        // the null was the answer every session got.
        //
        // What that broke: NavDatabase.UpgradeManager is
        //     upgradeManager ??= new NavDataUpgradeManager(SystemTenant.UpgradeMetadata, Tenant);
        // and the two-argument ctor chains through `tenant.Id` — the only dereference in its body,
        // since the workflow factory it also passes is a deferred lambda. So every caller of
        // NavSession.GetModuleExecutionContext / GetCurrentModuleExecutionContext died with a bare
        // NullReferenceException raised inside a BC ctor. That is reachable from ordinary AL:
        // BaseApp's Company-Initialize (codeunit 50) asks for the execution context from its
        // OnCompanyOpen subscriber, so any test that opens a company hit it. See AlRunner#2353.
        //
        // Note this is NOT the same method as NavSession.get_ExecutionContext, which
        // NclCecilRewrite already replaces with `return ExecutionContext.Normal` and whose comment
        // names this very NRE. That rewrite covers one method; the module-scoped siblings reach
        // Database.UpgradeManager through a different path and were left crashing.
        //
        // Populating the field rather than rewriting the property is what keeps the answer
        // faithful: BC's own GetModuleExecutionContext body still returns Install / Uninstall from
        // session.AppInstallationContext and Upgrade from session.AppUpgradeContext — neither of
        // which the runner populates yet, measured null inside install triggers (#4049) — and only falls through to
        // Normal when no upgrade workflow is in progress, which on the skeleton is always
        // (GetUpgradeInformation answers NavWorkflowState.NotStarted when no workflow was started).
        // A blanket "return Normal" replacement would answer Normal inside an install trigger,
        // where real BC answers Install.
        var fDatabaseTenant = _tNavDatabase.GetField("tenant",
            BindingFlags.NonPublic | BindingFlags.Instance);
        if (fDatabaseTenant == null)
        {
            Console.Error.WriteLine(
                "[RecordPatches] NavDatabase.tenant field NOT FOUND — NavDatabase.Tenant stays null, "
                + "NavSession.GetModuleExecutionContext will NRE");
        }
        else if (fDatabaseTenant.GetValue(_skeletonDatabase) == null)
        {
            var skeletonTenant = AlRunner.BcRuntime.SkeletonSystemTenant;
            if (skeletonTenant != null)
            {
                AlRunner.Infrastructure.FieldPoke.SetInstance(fDatabaseTenant, _skeletonDatabase, skeletonTenant);
                Console.Error.WriteLine("[RecordPatches] Skeleton NavDatabase.tenant wired to the skeleton system tenant");
            }
            else
            {
                // BcRuntime.InjectSkeletonSystemTenant runs before ApplyRecordPatches, so this
                // is only reachable when the environment ctor fell back and left Tenants null.
                Console.Error.WriteLine(
                    "[RecordPatches] No skeleton system tenant available — NavDatabase.Tenant stays null");
            }
        }

        // NavDatabase.tableConnectionSettingsStorage — BC's TableConnectionManager reads it on
        // every RegisterTableConnection (#2725). See TableConnectionPatches.
        TableConnectionPatches.PlantTableConnectionSettingsStorage(_skeletonDatabase, _tNavDatabase);

        Console.Error.WriteLine($"[RecordPatches] Skeleton NavDatabase built: {_skeletonDatabase.GetType().Name}");

        // DataAccessSource fields to poke when creating skeleton
        _fDasSession = _tDataAccessSource.GetField("session",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fDasGlobalFilters = _tDataAccessSource.GetField("globalFilters",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fDasTableVersionTokens = _tDataAccessSource.GetField("tableVersionTokens",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _fDasSessionTransactionManager = _tDataAccessSource.GetField("sessionTransactionManager",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Pre-build the singleton STM so all skeleton DAS instances share the same STM identity.
        _skeletonSessionTransactionManager = BuildSkeletonSessionTransactionManager();

        // TempTableDataProvider fields (for manual construction bypassing session.Database in ctor)
        var tTtdp = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.TempTableDataProvider")!;
        _fTtdpNavSession = tTtdp.GetField("navSession", BindingFlags.NonPublic | BindingFlags.Instance);
        _fTtdpTable = tTtdp.GetField("table", BindingFlags.NonPublic | BindingFlags.Instance);
        _fTtdpComparer = tTtdp.GetField("comparer", BindingFlags.NonPublic | BindingFlags.Instance);
        _fTtdpPrimaryKeySortingFields = tTtdp.GetField("primaryKeySortingFields",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _mTtdpFilter = tTtdp.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Filter" && m.GetParameters().Length == 5);
        if (_mTtdpFilter == null)
            Console.Error.WriteLine("[RecordPatches] WARN: TempTableDataProvider.Filter(5 params) not found");
        var tFieldDictGeneric = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.FieldDictionary`1");
        if (tFieldDictGeneric != null)
        {
            var tFieldDictNavValue = tFieldDictGeneric.MakeGenericType(typeof(NavValue));
            _ctorFieldDictionaryNavValue = tFieldDictNavValue.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters() is [{ ParameterType: { IsArray: true } }]);
            if (_ctorFieldDictionaryNavValue == null)
                Console.Error.WriteLine("[RecordPatches] WARN: FieldDictionary<NavValue>(Tuple[]) ctor not found");
        }

        // NCLMetaKey.SortingFieldsWithPrimaryKeyFields is internal — access via reflection
        var tNclMetaKey = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NCLMetaKey")!;
        _pNclMetaKeySortingFieldsWithPK = tNclMetaKey.GetProperty("SortingFieldsWithPrimaryKeyFields",
            BindingFlags.NonPublic | BindingFlags.Instance);

        // Pre-build and cache the collation comparer
        _collationComparer = BuildCollationAwareComparer();
        Console.Error.WriteLine($"[RecordPatches] Collation comparer built: {_collationComparer?.GetType().Name ?? "null"}");

        // Parse AL source files (tables + tableextensions + pages + reports + queries +
        // xmlports + object decls (AllObj) + object captions (AllObjWithCaption)) — see
        // ParseAllRegisteredSourceFiles for why this is ONE pass over _sourceDirs rather
        // than seven (#1903).
        ParseAllRegisteredSourceFiles();

        // NCL-internal system tables (RecordLink=2000000068, Field=2000000041, …)
        // live as AL source embedded in Microsoft.BusinessCentral.SystemApp.dll's
        // SystemPackage stream. BC's own NCL code constructs records of those tables
        // directly via `new NavRecord(parent, id)` — bypassing our NavRecordHandle
        // patch — so their NCLMetaTable must be in NCLMetadata's cache dict before
        // any test runs. Eagerly parse them here so the populator below picks them up.
        RegisterSystemAppPackage();

        // §O: lazy-populate the skeleton NCLMetadata cache with one NCLMetaTable
        // per parsed table so NavGlobal.NCLMetadata.GetMetaTableById / Codeunit.Run
        // call sites find an entry instead of throwing
        // NavNCLApplicationObjectNotFoundException.
        PopulateNclMetadataCache();

        // NavRecordHandle private field 'temp'
        var tRecHandle = nclAsm.GetType("Microsoft.Dynamics.Nav.Runtime.NavRecordHandle")!;
        _fNavRecordHandleTemp = tRecHandle.GetField("temp",
            BindingFlags.NonPublic | BindingFlags.Instance);
    }

    /// <summary>
    /// Replacement for NCLMetaTable.GetFieldByNo(int extensionObjectId, int fieldNo).
    /// BC compiled IL calls this overload for extension fields: e.g. GetFieldByNo(52800, 50100).
    /// Our skeleton NCLMetaTable has no extension objects registered, so the original throws
    /// NavNCLExtensionFieldNotFoundException. We fall back to TryGetFieldByNo(fieldNo, …) on
    /// the same instance, which succeeds because BuildNCLMetaTable already merged extension
    /// fields into allParsed (the base table's field list).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NCLMetaField NCLMetaTable_GetFieldByNoExt(NCLMetaTable self, int extensionObjectId, int fieldNo)
    {
        if (self.TryGetFieldByNo(fieldNo, out NCLMetaField? f) && f != null)
            return f;
        // Extension field unknown to us — throw the same exception BC would throw.
        throw new InvalidOperationException(
            $"[RecordPatches] extension field {fieldNo} from extension {extensionObjectId} not found in NCLMetaTable {self.TableName}");
    }

    /// <summary>
    /// Replacement for TempTableDataProvider.ctor(NavSession, NCLMetaTable).
    /// The real ctor calls navSession.Database.CollationAwareStringComparer which NREs on our
    /// skeleton session (no Tenant). We manually set all fields instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TempTableDataProviderCtorReplacement(object self, NavSession session, NCLMetaTable table)
    {
        _fTtdpNavSession?.SetValue(self, session);
        _fTtdpTable?.SetValue(self, table);
        // NCLMetaKey.SortingFieldsWithPrimaryKeyFields is internal — use reflection
        var pkSortingFields = _pNclMetaKeySortingFieldsWithPK?.GetValue(table.PrimaryKey);
        _fTtdpPrimaryKeySortingFields?.SetValue(self, pkSortingFields);
        _fTtdpComparer?.SetValue(self, _collationComparer);
    }

    // ─── Hook Implementations ───────────────────────────────────────────────────

    /// <summary>
    /// Replacement for NavRecordHandle.CreateTarget():
    /// bypasses NCLMetadata by constructing Record{ID} directly with a real NCLMetaTable
    /// built from parsed AL source.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static NavRecord NavRecordHandle_CreateTarget(NavRecordHandle self)
    {
        int id = self.ObjectId.ObjectNumber;
        bool isTemp = _fNavRecordHandleTemp != null && (bool)(_fNavRecordHandleTemp.GetValue(self) ?? false);

        var metaTable = (NCLMetaTable?)_metaTableCache.GetOrAdd(id, BuildNCLMetaTable);
        if (metaTable == null)
        {
            if (id == 0)
                AlRunner.Infrastructure.RunnerScope.ThrowNotYetImplemented(
                    "NavRecord.CloneForVariant (default-variant tableId=0)",
                    "HANDOFF.md §6 row E — synthetic empty NavRecord for default-variant clone case");
            throw new InvalidOperationException(
                $"NavRecordHandle.CreateTarget: no NCLMetaTable for table {id} (AL source not parsed)");
        }

        // Find Record{ID} : NavRecord in the loaded test assembly.
        var recordType = FindRecordType(id);
        if (recordType == null)
            throw new InvalidOperationException(
                $"NavRecordHandle.CreateTarget: no loaded type Record{id} found");

        var ctor = recordType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 6);
        if (ctor == null)
            throw new InvalidOperationException($"Record{id} has no 6-arg constructor");

        // Construct Record{ID}(parent, metaTable, isTemporary, sharedTable, companyName, securityFiltering)
        //
        // Validated, not Ignored: a Record variable created inside BC's test runner defaults to
        // SecurityFiltering.Validated — see corpus test
        // Codeunit60175.SecurityFiltering_Default_InTestContext_IsValidated_NotIgnored, which
        // asserts that contract explicitly. The distinction only became observable once
        // RecordImplementation.SetSecurityFiltering stopped being a no-op; before that the
        // argument passed here was discarded and the field kept its default.
        NavRecord rec;
        try
        {
            rec = (NavRecord)ctor.Invoke(new object?[] { self, metaTable, isTemp, null, null,
                SecurityFiltering.Validated });
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null)
        {
            // BC's own CreateTarget goes through table.CreateObjectInstance, a compiled
            // factory, so an exception raised while the record binds — BC's "table connection
            // ... must be registered" for a TableType = CRM table with no connection (#2725),
            // or a RunnerOutOfScopeException from the data-access route — reaches the AL
            // `asserterror` as itself. ConstructorInfo.Invoke wraps it instead, and AL then
            // read "Exception has been thrown by the target of an invocation." as the error
            // text. Rethrow the real one with its stack intact.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
        StampObjectId(rec, id);

        // Register any tableextensions on this (primary) record instance so the extension's
        // record-level triggers and field-validate triggers dispatch. CreateObjectInstance
        // handles the xRec/OldRecord and subtable instances; this is the path the test's own
        // `Rec: Record "…"` variable comes through, which would otherwise have no extensions.
        RegisterParsedTableExtensions(rec, id);
        // Wire this table's field OnValidate/OnLookup handlers + field-validate subscribers onto its
        // (built+cached) metatable. Both matter for tables built lazily at runtime — e.g. a precompiled
        // BaseApp table whose metatable did not exist when the startup passes ran: without the field
        // wiring its OnValidate body never runs (e.g. Purchase Header."Buy-from Vendor No." copying the
        // vendor name), without the subscriber injection an ISV's OnAfterValidateEvent never fires.
        WireFieldTriggerHandlersForTable(id, metaTable);
        AlRunner.Patches.EventSubscriberPatches.InjectValidateSubsForTable(id, metaTable);
        // Table-level trigger subscribers (Insert/Modify/Delete/Rename ordinals) — same lazy
        // wiring, called here (after GetOrAdd returned) rather than from inside BuildNCLMetaTable
        // to avoid the reentrant-GetOrAdd stack overflow described on InjectTriggerSubsForTable.
        AlRunner.Patches.EventSubscriberPatches.InjectTriggerSubsForTable(id, metaTable);
        return rec;
    }

    /// <summary>
    /// Replacement for NavSession.DataAccessSource getter.
    /// Returns a skeleton DataAccessSource backed by TempTableDataProvider (in-memory).
    /// </summary>
    /// <remarks>
    /// PER RECORD CONSTRUCTION -- NavRecord..ctor asks the session for its DataAccessSource, so
    /// this getter runs once per AL record variable that comes into existence. It used to open
    /// with an unconditional Console.Error.WriteLine, the same leftover tracing aid the IsOpen
    /// hook carried, and CPU sampling of the test in issue 2304 caught the two of them the
    /// same way. Behind BcRuntime.HookTraceEnabled now, which is read once.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object? NavSession_get_DataAccessSource(NavSession self)
    {
        if (BcRuntime.HookTraceEnabled)
            Console.Error.WriteLine("[RecordPatches] NavSession_get_DataAccessSource called");
        // Return cached DataAccessSource stored on the session's field.
        var existing = _fSessionDataAccessSource?.GetValue(self);
        if (existing != null) return existing;

        // Ensure the session has a skeleton NavDatabase — needed by TempTableDataProvider ctor.
        EnsureSkeletonDatabase(self);

        // Build a skeleton DataAccessSource.
        var das = RuntimeHelpers.GetUninitializedObject(_tDataAccessSource!);
        _fDasSession!.SetValue(das, self);
        _fDasGlobalFilters!.SetValue(das, Activator.CreateInstance(_tGlobalFilters!));
        _fDasTableVersionTokens!.SetValue(das, _mCreateForTempTable!.Invoke(null, null));

        // Pre-populate sessionTransactionManager — see InitializeSkeletonSession for
        // the full rationale. Keeps DataAccessSource.get_SessionTransactionManager out
        // of CreateAppDataAccess → CreateAppDataProvider (which NREs on no real DB).
        if (_skeletonSessionTransactionManager != null && _fDasSessionTransactionManager != null)
            _fDasSessionTransactionManager.SetValue(das, _skeletonSessionTransactionManager);

        // Cache it on the session field.
        _fSessionDataAccessSource?.SetValue(self, das);
        return das;
    }

    /// <summary>
    /// Replacement for DataAccessSource.GetDataAccessForTable(NCLMetaTable, bool).
    /// Uses one shared in-memory provider per (DataAccessSource,table) for regular tables,
    /// but returns a fresh provider for temporary records so each temp variable has an
    /// isolated buffer unless explicitly shared by BC semantics.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    /// <summary>
    /// Clear the per-(DataAccessSource,table) DataAccess cache. Called between test
    /// invocations so each test starts with empty tables, mirroring BC's per-test
    /// isolation transaction. Without this the in-memory store accumulates state
    /// across tests and Insert calls hit duplicate-key errors on common identifiers.
    /// </summary>
    // TEMPORARY (memory-census diagnostic) — total DataAccessSource entries and
    // summed per-table DataAccess entry counts across all of them. See MemoryCensus.cs.
    internal static (int sources, int tables) CensusDataAccessByTable()
    {
        int sources = 0, tables = 0;
        foreach (var (_, perTable) in _dataAccessByTable)
        {
            sources++;
            tables += perTable.Count;
        }
        return (sources, tables);
    }

    public static void ResetPerTestState()
    {
        // ConditionalWeakTable doesn't support Clear directly; the simplest correct
        // approach is to drain the per-DataAccessSource dictionaries in place.
        // The DataAccessSource itself is cached on _skeletonSession's DataAccessSource
        // backing field (a single instance), so iterating known sources is sufficient.
        foreach (var (_, perTable) in _dataAccessByTable)
            perTable.Clear();

        // Record links need no store of their own to reset: they live in the Record Link
        // table (2000000068), whose rows are cleared by the _dataAccessByTable drain above
        // and put back by the install-baseline restore, exactly like any other table (#3378).

        // IsolatedStorage in-memory store — per-test reset matches BC semantics where
        // a test's writes are rolled back on completion.
        AlRunner.Patches.TenantStoragePatches.ResetForTest();

        // MediaSet membership store — per-test reset matches BC semantics: a MediaSet
        // field's "Media Set" rows are as much part of the per-test transaction as any
        // other row, so they must not survive into the next test. See MediaSetPatches
        // file header (LIFETIME) for why this store needs an explicit reset instead of
        // relying on GC (the fix for #1773 keys it on a real, durable Guid rather than a
        // transient NavRecord instance, which is exactly what makes it durable — and
        // exactly why it needs this reset).
        AlRunner.Patches.MediaSetPatches.ResetForTest();

        // Write-transaction state behind Database.IsInWriteTransaction(). A test that
        // writes without committing must not leave the next test believing it started
        // inside a transaction — BC's per-test rollback ends the transaction either way.
        AlRunner.Patches.ALDatabasePatches.ResetWriteTransactionState();

        // Process-wide skeleton TreeSharedObjectContainer (SharedRecordRef / SharedNavStream
        // / SharedHttpRequest / SharedHttpResponseMessage / SharedNavHttpClient /
        // SharedNavObjectDictionary wrappers) — see BcRuntime.DisposeSkeletonSharedObjectContainerChildren
        // for why this is a distinct leak from _dataAccessByTable above and must be swept
        // at the same per-test boundary.
        AlRunner.BcRuntime.DisposeSkeletonSharedObjectContainerChildren();

        // SingleInstance=true codeunit instances are session-scoped in real BC and get reset
        // on the same per-test transaction rollback boundary as everything else above — without
        // this a SingleInstance codeunit's instance-variable state would leak from one test into
        // the next. See BcRuntime._singleInstanceCache / BcRuntime.ResetSingleInstanceCache.
        AlRunner.BcRuntime.ResetSingleInstanceCache();

        // Manual-binding event subscriptions (BindSubscription/Session.EventBindings) are
        // likewise a live-instance leak risk across the same boundary: a subscriber a test
        // bound and never unbound must not still be bound when the next test codeunit runs
        // (corpus-verified — TestEventManualBindingCrossCodeunit, 60244/60245 — a within-
        // codeunit leak across [Test] procedures IS faithful BC behaviour and is untouched
        // by this reset, since ResetPerTestState only runs at the codeunit/Test-isolation
        // boundary, not between methods sharing one codeunit instance). See #2466.
        AlRunner.BcRuntime.ResetEventBindingsForTestBoundary();
    }

    /// <summary>
    /// Replacement for NCLMetaApplicationObject.get_ApplicationObjectClrType.
    /// The real getter does lock(nclMetaObjectCLRTypeContainer) which NREs when the container
    /// is null (our CreateFromMetaTable-built tables never go through NCLCodeLoader).
    /// Instead, look up Record{ID} in the currently-loaded assemblies.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Type? NCLMetaApplicationObject_get_ApplicationObjectClrType(object self)
    {
        // Branch on ObjectType so this getter resolves correctly when the receiver
        // is an NCLMetaForm / NCLMetaReport (§P).  Tables are the §O default.
        if (!TryGetMetaObjectNumber(self, out var ot, out var id)) return null;
        return ot switch
        {
            // Page{id} first: that is what the AL compiler emits for a page, and answering
            // null here is not inert — NavEventSubscription's ctor calls GetScopeType on it
            // unguarded and NREs (#3436). Form{id} stays as a fallback rather than being
            // replaced, since it predates this and nothing measured which builds need it.
            "Page"     => FindClrTypeByName($"Page{id}") ?? FindClrTypeByName($"Form{id}"),
            // PageExtension{id} — what the AL compiler emits for a pageextension, and what
            // NCLPageExtension.IsTriggerImplemented<NavFormExtension> reads (#3447). Without
            // this arm a pageextension receiver fell to the table default and resolved
            // Record{id}, so BC's own CheckTrigger threw "OnOpenPage missing on Record{id}".
            "PageExtension" => FindClrTypeByName($"PageExtension{id}"),
            // TableExtension{id} — the table twin (#3556). BC reads this property on an
            // NCLTableExtension too (NCLMetaTable.DefinedTriggers walks orderedExtensionObjects
            // and asks each one), and without an arm such a receiver fell to the table default
            // and resolved Record{extId} — a different object, usually absent.
            "TableExtension" => FindTableExtensionType(id),
            "Report"   => FindClrTypeByName($"Report{id}"),
            "CodeUnit" => FindClrTypeByName($"Codeunit{id}"),
            _          => FindRecordType(id),
        };
    }

    // Metadata-backed lookup — see FindRecordTypeIn in RecordPatches.NclMetaTableBuilder.cs.
    private static Type? FindClrTypeByName(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = AlRunner.Infrastructure.AssemblyTypeIndex.For(asm).FindFirst(name);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Replacement for SequentialUuidCreator.NativeMethods.NewSequentialId.
    /// The original P/Invokes rpcrt4.dll!UuidCreateSequential which doesn't exist on Linux.
    /// Replace with a standard Guid.NewGuid() on all platforms.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Guid NewSequentialId_Replacement()
        => Guid.NewGuid();

}
