// RecordPatches.DataAccessDispatch — the one place a table id is turned into a DataAccess
// provider: DataAccessSource.GetDataAccessForTable and the virtual/system-table if-chain it
// dispatches through, plus the --test-data on-demand load hooks that chain hangs off.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;
public static partial class RecordPatches
{
    /// <summary>
    /// The --test-data on-demand load, installed by TestDataProvisioner.Arm() and null for
    /// every run that did not pass the flag (so a default run pays one null check per
    /// first-touch of a table). A delegate rather than a direct call because this file is the
    /// MECHANISM half: which tables a backup offers, and from which backup, is policy, and it
    /// lives in TestDataProvisioner. Arguments are (DataAccessSource, tableId).
    /// </summary>
    internal static Action<object, int>? TestDataOnDemandLoader;

    /// <summary>
    /// Told the id of a table whose storage was published from inside ANOTHER table's hydration
    /// and therefore could not be loaded there (#2877). Installed by TestDataProvisioner.Arm()
    /// alongside the loader, and null for every run without --test-data.
    ///
    /// The point is reporting, not mechanism: without it the provisioner has no record for that
    /// table at all, so TableOutcome answers null — which under the on-demand policy means
    /// "nothing in this run ever touched it", the opposite of what happened. Same argument as
    /// #2240.
    /// </summary>
    internal static Action<int>? TestDataDeferredLoadNotifier;

    /// <summary>
    /// Told (table id, reason) when a deferred load could not be run after all, because the
    /// store had rows by then or could not be read. Arriving here means the table ends the run
    /// WITHOUT its backup rows, so it is reported rather than skipped — a silent skip is what
    /// produced #2877. See .claude/rules/loud-failures.md.
    /// </summary>
    internal static Action<int, string>? TestDataDeferredLoadWriteOffNotifier;

    /// <summary>Re-entrancy depth for the loader — NOT a "which tables are loaded" cache,
    /// which #2262 rules out on purpose.
    ///
    /// Hydrating a table runs BC's own metadata and NavValue construction, and that code can
    /// reach a Record of ANOTHER table, which lands back in GetDataAccessForTableCore and
    /// would recurse.
    ///
    /// Skipping the nested load is only safe because the omission is RECORDED. The nested call
    /// has already published the nested table's storage by the time the load is refused, and
    /// "storage presence IS the have-we-loaded-this answer" then made the omission permanent:
    /// every later touch found the entry and never loaded it, so the table silently kept none
    /// of its backup rows for the whole run (#2877). GetOrCreateHydratedDataAccessCore's nested
    /// branch marks that instance as owing a load, and the next touch outside a materialisation
    /// settles it — see RecordPatches.TableMaterialisation.cs.</summary>
    [ThreadStatic] private static int _testDataLoadDepth;

    private static void InvokeTestDataOnDemandLoader(object source, int tableId)
    {
        var loader = TestDataOnDemandLoader;
        if (loader == null || _testDataLoadDepth > 0) return;
        _testDataLoadDepth++;
        try { loader(source, tableId); }
        finally { _testDataLoadDepth--; }
    }

    public static object NavDataAccessSource_GetDataAccessForTable(object self, NCLMetaTable table, bool isTemporary)
    {
        var dataAccess = GetDataAccessForTableCore(self, table, isTemporary);

        // Both branches below land on a TempTableDataProvider, so the provider alone
        // cannot say whether it is standing in for SQL or genuinely serving a
        // `temporary` record — and the two shapes disagree about whether an
        // uncommitted BLOB write reaches the stored row (corpus 60940, issue #1751).
        // This is the one place that still knows, so record it here.
        // A TableType = CRM table served by BC's CrmTestDataProvider is, in BC too, a
        // temp-token DataAccess over a TempTableDataProvider (CrmTableConnection.CreateDataAccess
        // passes DataAccessTableVersionTokens.CreateForTempTable) — not SQL-backed.
        if (!isTemporary && !TableConnectionPatches.IsExternalTableType(table, out _))
            BlobStoreIsolationPatches.MarkDatabaseBacked(dataAccess);

        return dataAccess;
    }

    private static object GetDataAccessForTableCore(object self, NCLMetaTable table, bool isTemporary)
    {
        try
        {
            if (isTemporary)
            {
                // A `temporary` record gets a fresh, private store and NONE of the
                // virtual-table populates below. Register it so the populates that run later
                // (at find/Get time, keyed only on table id) honour the same invariant this
                // early return does -- issue #2524.
                var tempDataAccess = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                _temporaryRecordDataAccess.AddOrUpdate(tempDataAccess, _temporaryRecordSentinel);
                return tempDataAccess;
            }

            // Per-(DataAccessSource, tableId) cache so Insert+Find on the same regular table
            // share storage.
            var perTable = _dataAccessByTable.GetValue(self,
                static _ => new ConcurrentDictionary<int, object>());
            var tableId = table.TableId;

            // ── External table types (CRM / ExternalSQL / Exchange / MicrosoftGraph) ─────
            // BC's own GetDataAccessForTable switches on table.TableType here and asks the
            // session's TableConnectionManager for the CURRENT connection of that type; the
            // connection builds the DataAccess. Every one of these used to fall through to the
            // temp store below as if it were a Normal table — a silent fake — because the
            // metadata layer mapped every TableType to Normal. With '@@test@@' registered the
            // CRM branch is BC's CrmTestDataProvider (Guid PK auto-assigned on insert); an
            // unregistered type is BC's own "not registered" error; a live connection is
            // refused by name. Not cached in perTable: the connection owns one provider per
            // table id itself (CrmTableConnection.testDataProviders), exactly as on a service
            // tier, and Unregister must drop it. See TableConnectionPatches (#2725).
            if (TableConnectionPatches.IsExternalTableType(table, out var externalTableType))
            {
                var externalSession = _fDasSession?.GetValue(self)
                    ?? throw new AlRunner.Infrastructure.RunnerOutOfScopeException(
                        $"Record {tableId} (TableType = {externalTableType})",
                        "table-connections — DataAccessSource has no skeleton session; see docs/scope.md",
                        "table-connections");
                return TableConnectionPatches.GetExternalDataAccess(
                    externalSession, table, externalTableType, _fDasGlobalFilters?.GetValue(self));
            }

            // ── Virtual Field system table (2000000041) ──────────────────────────────────
            // The Field table is virtual: the service tier computes its rows on the fly from
            // NCLMetadata (one row per NCLMetaField of the filtered TableNo) via the native
            // FieldDataProvider. Routing it to our empty in-memory store returns zero rows,
            // which makes BC code that iterates it (e.g. "Library - Workflow".EnableWorkflow,
            // Field.SetRange(TableNo,<t>); Field.FindSet()) throw "There is no Field within the
            // filter."
            //
            // The block below builds a managed Field-row provider: it populates our in-memory
            // store with REAL Field rows produced by BC's OWN managed row-builder
            // FieldDataProvider.GetFieldRecordBuffer (a pure NCLMetaField→NavValue[] projection,
            // NOT the crashing native find path) — see RecordPatches.FieldVirtualTable.cs. The
            // populate is faithful and works (hundreds of rows insert cleanly for every table).
            //
            // DEFAULT-ON. The subsequent `Field.FindSet()` is routed through a managed find
            // interception (a guard prepended to DataAccess.InnerFindAsync — see
            // RecordPatches.FieldFindIntercept.cs) that, for 2000000041 ONLY, runs the find
            // entirely in managed code (provider.Find → ResultSet → ResultSetEnumerator),
            // bypassing BC's native InnerFindAsync SQL transactional-cache prologue which AVs on
            // this virtual system table (its per-object SystemId/PK caches + table-version tokens
            // are never allocated — crash file-proven even with zero rows and TableType=Temporary).
            // The filtered TableNo's field rows are populated on demand at find time. Every other
            // table falls through to the original native InnerFindAsync unchanged.
            if (IsFieldVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var fieldDa))
                {
                    var created = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    fieldDa = perTable.GetOrAdd(tableId, created);
                }
                // Top up rows for every source table currently materialised (idempotent). The
                // subsequent Field.FindSet() is routed through DataAccess_FindAsync (a managed
                // bypass of BC's R2R InnerFindAsync, which AVs on this virtual system table) —
                // see RecordPatches.FieldFindIntercept.cs — and the filtered TableNo is populated
                // on demand there. This whole path is now DEFAULT-ON (no env gate): the find
                // interception means a populated Field table no longer crashes under R2R.
                var session = _fDasSession?.GetValue(self)
                    ?? throw FieldVirtualShapeGap("DataAccessSource has no skeleton session");
                PopulateFieldVirtualTable(fieldDa, table, session);
                return fieldDa;
            }

            // ── AllObj system virtual table (2000000038) ─────────────────────────────────
            // Also virtual on the service tier (AllObjDataProvider computes rows from
            // NCLMetadata.GetSnapshotOfAllObjects, whose body we Cecil-neuter because it
            // NREs on the skeleton NCLMetadata). Routed to the same in-memory store as
            // every other table, but POPULATED with one row per object the runner knows
            // about, so AllObj.Get(<type>, <id>) and filtered iteration answer truthfully
            // instead of always returning nothing.
            // See RecordPatches.AllObjVirtualTable.cs.
            if (IsAllObjVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var allObjDa))
                {
                    var createdAllObj = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    allObjDa = perTable.GetOrAdd(tableId, createdAllObj);
                }
                PopulateAllObjVirtualTable(allObjDa, table);
                return allObjDa;
            }

            // ── AllObjWithCaption system virtual table (2000000058) ──────────────────────
            // AllObj plus the Object Caption column, virtual for the same reason, and the
            // documented way for AL to render an object's caption (lookup pages bound to
            // it, TableRelation, and lookup(...) CalcFormula FlowFields). Same rows and
            // same key as AllObj — the inventory is literally shared, so the two tables
            // cannot disagree about which objects exist.
            // See RecordPatches.AllObjWithCaptionVirtualTable.cs.
            if (IsAllObjWithCaptionVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var allObjCaptionDa))
                {
                    var createdAllObjCaption = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    allObjCaptionDa = perTable.GetOrAdd(tableId, createdAllObjCaption);
                }
                PopulateAllObjWithCaptionVirtualTable(allObjCaptionDa, table);
                return allObjCaptionDa;
            }

            // ── Integer system virtual table (2000000026) ────────────────────────────────
            // Served by BC's OWN IntegerDataProvider, through BC's own
            // DataAccessSource.GetVirtualDataAccess — rows are computed per request and none
            // are stored, so an open-ended range answers on BC's ±1e9 clamp instead of from a
            // materialised window (#3485). Not cached in perTable: BC caches it per table id
            // on the DataAccessSource itself. See RecordPatches.IntegerVirtualTable.cs.
            if (IsIntegerVirtualTable(table))
            {
                return GetIntegerVirtualDataAccess(self, table);
            }

            // ── Table Relations Metadata system virtual table (2000000141) ──────────────
            // Served by BC's OWN TableRelationDataProvider, like Integer and Date above: rows are
            // computed per request from NCLMetaField.FieldRelations (#4088).
            // See RecordPatches.TableRelationsMetadataVirtualTable.cs.
            if (IsTableRelationsMetadataVirtualTable(table))
            {
                return GetTableRelationsMetadataVirtualDataAccess(self, table);
            }

            // ── All Profile system virtual table (2000000178) ────────────────────────────
            // Virtual on the service tier too: AllProfileDataProvider's rows are every
            // profile every published app declares plus the tenant-owned ones. It is the
            // table Profile List / Profile Card are bound to and the one
            // Conf./Personalization Mgt. resolves a user's role centre through, so an empty
            // store made every read of it raise "There is no All Profile within the filter."
            // Populated once per provider — unlike AllObj this table is WRITTEN by AL, and a
            // top-up on a later handout would resurrect a just-deleted row.
            // See RecordPatches.AllProfileVirtualTable.cs.
            if (IsAllProfileVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var allProfileDa))
                {
                    var createdAllProfile = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    allProfileDa = perTable.GetOrAdd(tableId, createdAllProfile);
                }
                PopulateAllProfileVirtualTable(allProfileDa, table);
                return allProfileDa;
            }

            // ── Date system virtual table (2000000007) ───────────────────────────────────
            // Served by BC's OWN DateDataProvider, through BC's own
            // DataAccessSource.GetVirtualDataAccess — one row per period is computed per
            // request and none are stored, so a range open at one end runs out to BC's own
            // first/last period start instead of stopping at a materialised window (#3506).
            // Not cached in perTable: BC caches it per table id on the DataAccessSource
            // itself. See RecordPatches.DateVirtualTable.cs.
            if (IsDateVirtualTable(table))
            {
                return GetDateVirtualDataAccess(self, table);
            }

            // ── Report Layout List system virtual table (2000000234) ─────────────────────
            // Virtual on the service tier too (its rows are the layouts every published
            // app declares, plus tenant layouts). BC's own by-name layout resolution
            // (ReportLayoutSelection.GetLayoutByNameAndAppIDAsync) reads exactly this
            // table, so populating it from the compiler-captured `rendering { layout(…) }`
            // declarations makes selection-by-name work through BC's own code path.
            // See RecordPatches.ReportLayoutListVirtualTable.cs.
            if (IsReportLayoutListVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var layoutDa))
                {
                    var createdLayout = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    layoutDa = perTable.GetOrAdd(tableId, createdLayout);
                }
                PopulateReportLayoutListVirtualTable(layoutDa, table);
                return layoutDa;
            }

            // ── Report Metadata (2000000139) / Report Data Items (2000000203) ────────────
            // Virtual on the service tier too: their rows are computed from the metadata of
            // every published report. They are the documented way for AL to discover a
            // report's caption, request-page flag and dataset shape without running it, and
            // an empty store makes every such lookup answer "no such report" / "no data
            // items". See RecordPatches.ReportMetadataVirtualTable.cs.
            if (IsReportMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var reportMetaDa))
                {
                    var createdReportMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    reportMetaDa = perTable.GetOrAdd(tableId, createdReportMeta);
                }
                PopulateReportMetadataVirtualTable(reportMetaDa, table);
                return reportMetaDa;
            }

            // ── Metadata Permission Set system virtual table (2000000250) ───────────────
            // Virtual on the service tier too (MetadataPermissionSetDataProvider computes
            // its rows from the permission sets the installed apps declare). An empty store
            // makes Microsoft's own "Users - Create Super User" (codeunit 9000) fail its
            // `MetadataPermissionSet.Get(<null guid>, 'SUPER')`, so every AL test that
            // creates a user dies before it starts (issue #2313).
            // See RecordPatches.MetadataPermissionSetVirtualTable.cs.
            if (IsMetadataPermissionSetVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var permSetDa))
                {
                    var createdPermSet = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    permSetDa = perTable.GetOrAdd(tableId, createdPermSet);
                }
                PopulateMetadataPermissionSetVirtualTable(permSetDa, table);
                return permSetDa;
            }

            // ── Permission Set system table (2000000004) ────────────────────────────────
            // Declared as an ordinary (obsolete-pending) table, but a modern tier does not
            // read it from SQL: DataAccessSource.GetVirtualDataProvider routes it to
            // PermissionSetDataProvider, which computes one row per ASSIGNABLE permission
            // set the installed apps declare. An empty store made Microsoft's own
            // "Permissions Mock"(codeunit 131006).Assign fail its PermissionSet.Get(RoleID),
            // which is every named entry point of "Library - Lower Permissions" (issue
            // #3344). See RecordPatches.PermissionSetSystemTable.cs.
            if (IsPermissionSetSystemTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var permSetSysDa))
                {
                    var createdPermSetSys = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    permSetSysDa = perTable.GetOrAdd(tableId, createdPermSetSys);
                }
                // The assignable-only shape — what walking the table lists. A keyed Get()
                // repopulates with the non-assignable sets as well, through
                // DataAccess_PermissionSetSystemTableGuardForGet.
                PopulatePermissionSetSystemTable(permSetSysDa, table, includeNonAssignable: false);
                return permSetSysDa;
            }

            // ── Permission system virtual table (2000000005) ────────────────────────────
            // Virtual on the service tier too: PermissionDataProvider computes one row per
            // permission a permission set grants, from the same metadata inventory the two
            // tables above read. An empty store made every `Permission.Get(role, type, id)`
            // answer "does not exist" and every filtered walk come back empty, while the
            // set's HEADER listed correctly one table over — so a set's grants were
            // unreadable with nothing reporting a gap (#3695).
            // See RecordPatches.PermissionSystemTable.cs.
            if (IsPermissionSystemTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var permDa))
                {
                    var createdPerm = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    permDa = perTable.GetOrAdd(tableId, createdPerm);
                }
                var permSession = _fDasSession?.GetValue(self)
                    ?? throw PermissionSystemTableShapeGap(
                        "DataAccessSource has no skeleton session, so BC's own "
                        + "PermissionDataProvider cannot be constructed");
                PopulatePermissionSystemTable(permDa, table, permSession);
                return permDa;
            }

            // ── Aggregate Permission Set system virtual table (2000000167) ──────────────
            // Virtual on the service tier too: its rows are the UNION of System-scope
            // (Metadata Permission Set, 2000000250 — just above) and Tenant-scope (Tenant
            // Permission Set, 2000000165) rows, computed by BC's own
            // AggregatePermissionSetDataProvider, driven directly by reflection. An empty
            // store made every `Record "Aggregate Permission Set".Get(...)` fail "does not
            // exist" — the root of a 14-test "already bound" cascade in Microsoft's own
            // Tests-SINGLESERVER bucket (issue #2357, ruled out as a binding-mechanism
            // defect by #2393). See RecordPatches.AggregatePermissionSetVirtualTable.cs.
            if (IsAggregatePermissionSetVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var aggPermSetDa))
                {
                    var createdAggPermSet = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    aggPermSetDa = perTable.GetOrAdd(tableId, createdAggPermSet);
                }
                var aggPermSetSession = _fDasSession?.GetValue(self)
                    ?? throw AggregatePermissionSetShapeGap(
                        "DataAccessSource has no skeleton session, so BC's own "
                        + "AggregatePermissionSetDataProvider cannot be constructed");
                PopulateAggregatePermissionSetVirtualTable(aggPermSetDa, table, aggPermSetSession);
                return aggPermSetDa;
            }

            if (IsReportDataItemsVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var reportDiDa))
                {
                    var createdReportDi = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    reportDiDa = perTable.GetOrAdd(tableId, createdReportDi);
                }
                PopulateReportDataItemsVirtualTable(reportDiDa, table);
                return reportDiDa;
            }

            // ── Table Metadata (2000000136) ──────────────────────────────────────────────
            // Virtual on the service tier too: one row per table in the application. An
            // empty store makes every lookup answer "no such table", which is what broke
            // Base App "Page Management".GetDefaultLookupPageID on custom tables.
            // See RecordPatches.TableMetadataVirtualTable.cs.
            if (IsTableMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var tableMetaDa))
                {
                    var createdTableMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    tableMetaDa = perTable.GetOrAdd(tableId, createdTableMeta);
                }
                PopulateTableMetadataVirtualTable(tableMetaDa, table);
                return tableMetaDa;
            }

            // ── Page Metadata (2000000138) ───────────────────────────────────────────────
            // Virtual on the service tier too: one row per page in the application. An
            // empty store makes every lookup answer "no such page", which is what broke
            // Base App "Page Management".GetDefaultCardPageID's SourceTable+PageType scan
            // fallback for tables declaring no LookupPageId. See
            // RecordPatches.PageMetadataVirtualTable.cs (#1769).
            if (IsPageMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var pageMetaDa))
                {
                    var createdPageMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    pageMetaDa = perTable.GetOrAdd(tableId, createdPageMeta);
                }
                PopulatePageMetadataVirtualTable(pageMetaDa, table);
                return pageMetaDa;
            }

            // ── Time Zone (2000000164) ───────────────────────────────────────────────────
            // Virtual on the service tier too (TimeZoneDataProvider enumerates the HOST's
            // TimeZoneInfo.GetSystemTimeZones() and numbers them 1..N). An empty store made
            // every read answer "no such time zone" silently. The runner enumerates the same
            // host call BC does, which on Linux means IANA ids where a Windows-hosted tier
            // reports Windows ids — a deliberate, permanent divergence recorded in
            // docs/limitations.md. See RecordPatches.TimeZoneVirtualTable.cs (#2584).
            if (IsTimeZoneVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var timeZoneDa))
                {
                    var createdTimeZone = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    timeZoneDa = perTable.GetOrAdd(tableId, createdTimeZone);
                }
                PopulateTimeZoneVirtualTable(timeZoneDa, table);
                return timeZoneDa;
            }

            // ── Session (2000000009) ─────────────────────────────────────────────────────
            // Virtual on the service tier too, and SessionDataProvider returns exactly ONE
            // row — the reading session, My Session = true — not one per logged-on user.
            // Routed to the same in-memory store as every other virtual table here and
            // populated with that one row, read back from the skeleton NavSession so the
            // table cannot disagree with SessionId() / UserId(). An empty store made every
            // read answer "nobody is logged on", which is a wrong answer rather than a
            // missing one. See RecordPatches.SessionVirtualTable.cs (#2940).
            if (IsSessionVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var sessionDa))
                {
                    var createdSession = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    sessionDa = perTable.GetOrAdd(tableId, createdSession);
                }
                var sessionTableSession = _fDasSession?.GetValue(self)
                    ?? throw SessionVirtualShapeGap(
                        "DataAccessSource has no skeleton session, so there is no session "
                        + "identity to read the row back from");
                PopulateSessionVirtualTable(sessionDa, table, sessionTableSession);
                return sessionDa;
            }

            // ── Feature Key (2000000211) ─────────────────────────────────────────────────
            // Routed to BC's OWN FeatureKeyDataProvider: its feature list is a hardcoded static
            // in Microsoft.Dynamics.Nav.Types, so the rows are BC's rather than a second copy
            // that would drift. An empty store made Base Application's Feature Management read
            // every feature as unregistered and silently win the legacy code path.
            // NOT a claim about any specific feature's shipped state: the 28.1 set measured
            // here is 14 features, every one State = None. An earlier version of this comment
            // said CalcOnlyVisibleFlowFields ships AllUsers (ON); that was read off the wrong
            // Types.dll and is false for 28.1/28.4. Measure BuildFeatureKeys() against the
            // artifact under test before asserting any feature's state.
            // POPULATED READ-ONLY: real BC's Modify writes new state through to table
            // 2000000210, which is not implemented here, so a Modify would land in the temp
            // store and go nowhere. Issue #2585 tracks the write path.
            // See RecordPatches.FeatureKeyVirtualTable.cs (#2585).
            if (IsFeatureKeyVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var featureKeyDa))
                {
                    var createdFeatureKey = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    featureKeyDa = perTable.GetOrAdd(tableId, createdFeatureKey);
                }
                var featureKeySession = _fDasSession?.GetValue(self)
                    ?? throw FeatureKeyShapeGap(
                        "DataAccessSource has no skeleton session, so BC's own "
                        + "FeatureKeyDataProvider cannot be constructed");
                PopulateFeatureKeyVirtualTable(featureKeyDa, table, featureKeySession);
                return featureKeyDa;
            }

            // ── Windows Language (2000000045) ────────────────────────────────────────────
            // Virtual on the service tier too (WindowsLanguageDataProvider iterates BC's own
            // WindowsLanguageHelper.AllCultures). An empty store made every language lookup
            // answer "no such language" silently. The six license-derived columns and the four
            // installed-resource columns throw instead of guessing — see
            // RecordPatches.WindowsLanguageVirtualTable.cs (#2581).
            if (IsWindowsLanguageVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var windowsLanguageDa))
                {
                    var createdWindowsLanguage = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    windowsLanguageDa = perTable.GetOrAdd(tableId, createdWindowsLanguage);
                }
                PopulateWindowsLanguageVirtualTable(windowsLanguageDa, table);
                return windowsLanguageDa;
            }

            // ── NAV App Extra (2000000157) ───────────────────────────────────────────────
            // Virtual on the service tier too (NavAppExtraDataProvider computes one row per
            // app from the session's app metadata). An empty store made Published
            // Application's "Tenant Visible" and "PerTenant Or Installed" Lookup FlowFields
            // read FALSE for every app — the runner answering "no app is visible to this
            // tenant", which is a wrong answer and not a missing one, and the same
            // Boolean-default shape a real tier already contradicted once for the Installed
            // FlowField next door (#3066). BC's own provider is tried first; the fallback
            // builds the rows from the same loaded-module list and the same
            // AppPackageIdentity values as the Published Application seeder, so the two
            // tables cannot disagree about which app a runtime package id names.
            // See RecordPatches.NavAppExtraVirtualTable.cs (#3072).
            if (IsNavAppExtraVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var navAppExtraDa))
                {
                    var createdNavAppExtra = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    navAppExtraDa = perTable.GetOrAdd(tableId, createdNavAppExtra);
                }
                var navAppExtraSession = _fDasSession?.GetValue(self)
                    ?? throw NavAppExtraShapeGap(
                        "DataAccessSource has no skeleton session, so BC's own "
                        + "NavAppExtraDataProvider cannot be constructed");
                PopulateNavAppExtraVirtualTable(navAppExtraDa, table, navAppExtraSession);
                return navAppExtraDa;
            }

            // ── CodeUnit Metadata (2000000137) ───────────────────────────────────────────
            // Virtual on the service tier too (CodeUnitDataProvider computes one row per
            // codeunit from NCLMetadata). An empty store makes every lookup answer "no such
            // codeunit", so Get() silently returns false and FindSet() raises — and a
            // TableRelation to this table refuses a codeunit that really is in the run.
            // The last missing member of the Table/Page/Report Metadata family above.
            // See RecordPatches.CodeunitMetadataVirtualTable.cs (#2544).
            if (IsCodeunitMetadataVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var codeunitMetaDa))
                {
                    var createdCodeunitMeta = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    codeunitMetaDa = perTable.GetOrAdd(tableId, createdCodeunitMeta);
                }
                PopulateCodeunitMetadataVirtualTable(codeunitMetaDa, table);
                return codeunitMetaDa;
            }

            // ── Object Metadata (2000000071) ─────────────────────────────────────────────
            // NOT a virtual table: a real application-database system table, read with plain
            // SQL by Ncl's own ObjectMetadataStorage. The runner has no application database,
            // so its store was empty and a FindLast raised "There is no Object Metadata
            // within the filter" — which is how it takes out Microsoft's own
            // Codeunit136608.VerifyValidatePackageCodeunitFailed (#2519).
            //
            // Because the table IS real, a --test-data backup can genuinely carry rows for it.
            // So the on-demand loader runs FIRST on a freshly created store, and the populator
            // below does nothing when the store already holds a row: real rows win, synthesis
            // is the fallback. Every other branch in this method serves a table no backup can
            // ever have rows for, which is why only this one loads before populating.
            //
            // That precedence needs the hand-out to be ordered as well as the load, or a second
            // thread is given the store between "created" and "hydrated", finds it empty and
            // synthesises over rows that are about to arrive (#2788). GetOrCreateHydratedDataAccess
            // is what guarantees it — see RecordPatches.TableMaterialisation.cs — so the populate
            // below always runs on a store that is either hydrated or never will be.
            // See RecordPatches.ObjectMetadataSystemTable.cs.
            //
            // A store published by a NESTED materialisation is the one case where the populate
            // must NOT run yet: it owes a --test-data load that could not run there, and
            // synthesising into it now would make that load a mix of real and synthesised rows.
            // MaterialiseObjectMetadataStore holds the populate off until the debt is settled
            // (#2877) — with no loader installed nothing is ever owed and this is unchanged.
            if (IsObjectMetadataSystemTable(table))
                return MaterialiseObjectMetadataStore(self, perTable, table, tableId);

            // ── Object (2000000001) HAS NO BRANCH HERE, ON PURPOSE (#3071) ───────────────
            // It used to have one, projecting the runner's object inventory into the legacy
            // registry so that Object Metadata."Object ID"'s declared
            // TableRelation = Object.ID WHERE(Type = FIELD("Object Type")) pointed at
            // something (#2774). A real service tier then measured the table: corpus codeunit
            // 61202 (StefanMaron/BusinessCentral.AL.Language.Tests#197) found it present,
            // readable and EMPTY on seven BC OnPrem legs, with a control arm reading the
            // populated sibling in the same session so "empty" could not be an unreadable
            // table. A declared relation is not evidence that its target is populated.
            //
            // So this table now takes the generic fall-through at the end of this method, the
            // one every other application-database table takes. That is deliberate rather than
            // incidental: the fall-through is the SAME GetOrCreateHydratedDataAccess call the
            // deleted branch made, so a --test-data backup's real rows still land, in the same
            // order, with the same #2788 hand-out guarantee — only the synthesis is gone.
            // See RecordPatches.ObjectSystemTable.cs for the measurement.

            // ── Page Control Field (2000000192) ──────────────────────────────────────────
            // Virtual on the service tier too: one row per field control declared on a
            // page, INCLUDING controls declared Visible = false. An empty store made every
            // filtered query answer "no rows" silently (no error), so a test asserting a
            // control is absent would have passed against a broken provider too. See
            // RecordPatches.PageControlFieldVirtualTable.cs (#1779).
            if (IsPageControlFieldVirtualTable(table))
            {
                if (!perTable.TryGetValue(tableId, out var pageControlFieldDa))
                {
                    var createdPageControlField = _mCreateTempDataAccess!.Invoke(self, new object[] { table })!;
                    pageControlFieldDa = perTable.GetOrAdd(tableId, createdPageControlField);
                }
                PopulatePageControlFieldVirtualTable(pageControlFieldDa, table);
                return pageControlFieldDa;
            }

            // ── --test-data on-demand load (#2262) ───────────────────────────────────────
            // A store that does not have this table yet is exactly when a --test-data run needs
            // its rows read out of the backup. Same choke point the virtual tables above use,
            // and for the same reason: it is the only place a table's storage is materialised,
            // so the load always lands before the operation that triggered it — a read and a
            // write are equally covered.
            //
            // Storage presence IS the "have we loaded this" answer, so there is no flag to
            // keep in step: RestoreInstallBaselineSnapshot repopulates perTable from exactly
            // the snapshot it restores, so a table the last restore carried is present and
            // does not reach the create below. See TestDataProvisioner's header.
            //
            // Only the GetOrAdd winner loads — a loser would be hydrating into storage it is
            // about to throw away — and no racer is handed the storage until that load has
            // finished, so a caller here can never act on a half-hydrated table (#2788).
            // See RecordPatches.TableMaterialisation.cs.
            return GetOrCreateHydratedDataAccess(self, perTable, table, tableId);
        }
        catch (Exception ex)
        {
            var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            // Header + trace in ONE tagged write: Log.FilteredWriter matches per write and
            // a bare stack trace has no `[Component]` tag, so a second call would print its
            // frames at default verbosity under a header the filter had just dropped.
            Console.Error.WriteLine(
                $"[RecordPatches] GetDataAccessForTable failed for table "
                + $"{table?.TableName ?? "null"}: {inner.GetType().Name}: {inner.Message}"
                + $"\n{inner.StackTrace}");
            throw;
        }
    }
}
