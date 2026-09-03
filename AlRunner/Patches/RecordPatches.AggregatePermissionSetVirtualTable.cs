// RecordPatches.AggregatePermissionSetVirtualTable — managed provider for the
// "Aggregate Permission Set" system virtual table (2000000167).
//
// WHY THIS EXISTS
//   On a real service tier Aggregate Permission Set is a VIRTUAL table: its rows are
//   computed by Microsoft.Dynamics.Nav.Runtime.AggregatePermissionSetDataProvider (an
//   EagerVirtualDataProvider) as the UNION of two other tables' rows —
//     Scope::System  from "Metadata Permission Set" (2000000250, when
//                    ServerUserSettings.Instance.UsePermissionSetsFromExtensions is
//                    true — the modern, ConfigSetting-default architecture) or the
//                    legacy "Permission Set" (2000000004) otherwise,
//     Scope::Tenant  from "Tenant Permission Set" (2000000165),
//   with an "App Name" column resolved by looking the row's App ID up in the current
//   NavAppGroup's installed-app metadata.
//
//   Our runtime routes every table's data access through
//   NavDataAccessSource_GetDataAccessForTable → an in-memory TempTableDataProvider, and
//   for 2000000167 that store was empty. So `Record "Aggregate Permission Set".Get(...)`
//   always raised "does not exist" — Microsoft's own Tests-SINGLESERVER bucket hits this
//   in Codeunit134614 "Test App Permissions", TestAggregatePermissionSetsTable, whose own
//   failure here (unrelated to event binding) is the ROOT of that codeunit's 14-test
//   "already bound" cascade investigated and ruled out as a binding-mechanism defect in
//   issue #2393 — see #2357.
//
// WHAT THIS DOES (faithful, managed, R2R-safe)
//   Rather than re-deriving the union logic (and risking it drifting from BC's own idea of
//   what belongs in each scope), we drive BC's REAL, unmodified
//   AggregatePermissionSetDataProvider methods by reflection: GetSystemPermissionSets()
//   (reads table 2000000250, already served faithfully by
//   RecordPatches.MetadataPermissionSetVirtualTable.cs, issue #2313/#2330, through a real
//   NavRecord — the SAME lazy-populate dispatch this table itself goes through) and
//   GetTenantPermissionSets() (reads "Tenant Permission Set", 2000000165, a normal,
//   already-working table), then CreateRecordBuffer() per item — every column laid out
//   exactly the way BC lays it out (Scope option value, App ID, Role ID, Name, App Name,
//   plus BC's system slots).
//
//   This is deliberately NOT a single call to GetAllItems()/GetAllItemsInternal(). That
//   method is one continuous C# iterator over BOTH scopes, and CreateRecordBuffer — called
//   INSIDE it, per item — can throw for a single bad row (see IsRoleIdTooLongForAggregateTable
//   below). A C# compiler-generated iterator that throws out of MoveNext() transitions to a
//   terminal "finished" state: every FURTHER MoveNext() call on that SAME enumerator
//   returns false, not "resume after the bad item". A single bad row therefore silently
//   truncates the ENTIRE remaining union — not just the offending item, but every row after
//   it in GetSystemPermissionSets() AND the whole of GetTenantPermissionSets() (Concat'd
//   after it) — which is exactly what happened when this file first tried a manual
//   MoveNext() loop around GetAllItems() and skip-via-catch-and-continue: one skip silently
//   dropped ~490 of ~520 rows, including this run's own `TestSet`/`TestSet2` bundle
//   permission sets and the entire Tenant scope. Draining GetSystemPermissionSets() and
//   GetTenantPermissionSets() to completion FIRST (neither constructs a new NavCode or
//   otherwise risks this throw — see below) and calling CreateRecordBuffer() afterward, one
//   ORDINARY (non-iterator) method call per item, means a throw for one item is a normal
//   try/catch around one call and cannot corrupt any other item's turn.
//
// PRECOMPILED-DLL RESPECT
//   AggregatePermissionSetDataProvider, EagerVirtualDataProvider, NCLMetadata, NCLMetaTable,
//   NavValue, ReadOnlyRecordBuffer and TempTableDataProvider are all runtime-engine types
//   (Ncl.dll) — none of this touches an AL-business-logic DLL body. Codeunit134614 (Tests-
//   SINGLESERVER, Microsoft's own AL) runs completely unmodified; only the metadata under it
//   changes, exactly as RecordPatches.MetadataPermissionSetVirtualTable.cs does for its
//   sibling table.
//
// LIVE, NOT SNAPSHOTTED (issue #2473)
//   An earlier shape of this file populated the store ONCE per provider (a
//   ConditionalWeakTable-guarded flag) and never again. That was faithful the moment of
//   first touch, but "Tenant Permission Set" (2000000165) is an ordinarily-writable table —
//   AL can Insert/Delete/Rename its rows at any time — and on a real service tier every
//   later Get()/FindSet() against Aggregate Permission Set re-derives the union fresh, so a
//   row inserted after the first touch IS visible there. The one-shot guard made it
//   invisible here instead: the actual root of the 14-test "already bound" cascade in
//   Codeunit134614, not the event-binding mechanism #2393 ruled out. Every touch now clears
//   this table's OWN store (ClearProviderInPlace, RecordPatches.TransactionSnapshot.cs —
//   Tenant/Metadata Permission Set themselves are untouched) and redrives the whole union,
//   so a later delete/rename of the underlying Tenant Permission Set row cannot leave a
//   ghost behind either — a top-up-only shape (inserting only NEW keys, never removing
//   stale ones) would have exactly that gap, the same asymmetry
//   RecordPatches.AllProfileVirtualTable.cs's own banner documents for a table backed by
//   AL-writable data.
//
//   RecordPatches.MetadataPermissionSetVirtualTable.cs's sibling `_mpsPopulatedByProvider`
//   guard was audited for the same shape and does NOT have this gap: its rows come from
//   ParsedPermissionSets (this run's own compiled AL source) and each dependency .app's
//   SymbolReference.json (EnumerateKnownPermissionSets) — both fixed for the lifetime of one
//   runner invocation, not runtime-writable AL data, so a repeated top-up there can only
//   ever re-discover the SAME fixed set, never miss a write that happened after first touch.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int AggregatePermissionSetVirtualTableId = 2000000167;

    private static bool _apsReflectionReady;
    private static Type? _apsProviderType;                    // Microsoft.Dynamics.Nav.Runtime.AggregatePermissionSetDataProvider
    private static ConstructorInfo? _apsProviderCtor;          // .ctor(NavSession, NCLMetadata)
    private static MethodInfo? _apsGetSystemPermissionSets;    // private IEnumerable<PermissionSetRecord> GetSystemPermissionSets(NavValue, NavCode)
    private static MethodInfo? _apsGetTenantPermissionSets;    // private IEnumerable<PermissionSetRecord> GetTenantPermissionSets(NavValue, NavCode)
    private static MethodInfo? _apsCreateRecordBuffer;         // private ReadOnlyRecordBuffer CreateRecordBuffer(PermissionSetRecord, string)
    private static FieldInfo? _apsRecordKeyField;               // PermissionSetRecord.permissionSetKey
    private static PropertyInfo? _apsKeyAppIdProp;               // PermissionSetKey.AppId (Guid)
    private static PropertyInfo? _apsSessionNclMetadata;        // NavSession.NCLMetadata

    private static bool IsAggregatePermissionSetVirtualTable(NCLMetaTable? table)
        => table != null && table.TableId == AggregatePermissionSetVirtualTableId;

    /// <summary>
    /// Populate the in-memory store behind the Aggregate Permission Set (2000000167) data
    /// access by driving BC's own <c>AggregatePermissionSetDataProvider</c> for the
    /// skeleton session, one time per provider instance.
    /// </summary>
    private static void PopulateAggregatePermissionSetVirtualTable(object dataAccess, NCLMetaTable metaTable, object session)
    {
        EnsureAllObjReflection(metaTable);
        EnsureAggregatePermissionSetReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var store = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Aggregate Permission Set (virtual table 2000000167)",
                "aggregate-permission-set-virtual-table — data access has no in-memory provider; see docs/scope.md");

        // Recompute the WHOLE union fresh on every touch — on a real service tier
        // Aggregate Permission Set has no persistent state of its own; every Get()/FindSet()
        // re-derives from Metadata Permission Set and Tenant Permission Set at that moment
        // (EagerVirtualDataProvider, see the file banner). An earlier shape here gated on a
        // one-shot "populate once" flag per provider, so the SECOND and every later touch
        // was a no-op: a Tenant Permission Set row inserted after the first touch never
        // appeared (#2473) — the actual root of a 14-test cascade in Microsoft's own
        // Tests-SINGLESERVER Codeunit134614 (#2357/#2393).
        //
        // Clearing this table's OWN store (not Tenant/Metadata Permission Set, which are
        // untouched) before every redrive, rather than a top-up-only insert, also keeps a
        // later Tenant Permission Set DELETE/RENAME from leaving a ghost row behind here —
        // the same asymmetric gap RecordPatches.AllProfileVirtualTable.cs's own banner
        // warns a top-up-only shape would have for a table backed by AL-writable data.
        // ClearProviderInPlace (RecordPatches.TransactionSnapshot.cs) only nulls the row
        // trees, not the "table" metadata field, so the provider stays usable afterward.
        ClearProviderInPlace(store);

        var nclMetadata = _apsSessionNclMetadata!.GetValue(session)
            ?? throw new RunnerOutOfScopeException(
                "Aggregate Permission Set (virtual table 2000000167)",
                "aggregate-permission-set-virtual-table — NavSession.NCLMetadata is null on the "
                + "skeleton session, so BC's own AggregatePermissionSetDataProvider cannot resolve "
                + "the System/Tenant Permission Set tables it unions; see docs/scope.md");

        object bcProvider;
        List<object> systemRecords;
        List<object> tenantRecords;
        try
        {
            bcProvider = _apsProviderCtor!.Invoke(new object?[] { session, nclMetadata });
            // Neither method constructs a NEW NavCode or otherwise risks the length-overflow
            // throw CreateRecordBuffer can raise (see the file banner) — both just read
            // fields already stored in an existing record and cast/yield them — so draining
            // each to a List here is safe regardless of what CreateRecordBuffer does later
            // with any one item.
            systemRecords = DrainToList(_apsGetSystemPermissionSets!.Invoke(bcProvider, new object?[] { null, null })!);
            tenantRecords = DrainToList(_apsGetTenantPermissionSets!.Invoke(bcProvider, new object?[] { null, null })!);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable — satisfies the compiler's flow analysis
        }

        // "App Name" resolution: BC's own GetAllItemsInternal looks the row's App ID up in
        // NavCurrentThread.ResolveAppGroup().OrderedAppMetadata — a type this file would
        // otherwise need three more reflection surfaces just to read a display column no
        // failing test in this cluster asserts a specific literal value for (Codeunit134614
        // only ever compares AggregatePermissionSet."App Name" against a page control bound
        // to the SAME row, never against a hardcoded string). The runner already knows every
        // App ID -> App Name mapping it can possibly serve a permission set row for — it is
        // the exact same inventory RecordPatches.MetadataPermissionSetVirtualTable.cs's own
        // EnumerateKnownPermissionSets() draws from — so building it from there is truthful,
        // not a stand-in: any App ID this table can report a row for has a known name here.
        var appNames = BuildKnownAppNameIndex();

        foreach (var record in systemRecords) InsertAggregatePermissionSetRecord(bcProvider, store, record, appNames);
        foreach (var record in tenantRecords) InsertAggregatePermissionSetRecord(bcProvider, store, record, appNames);
    }

    /// <summary>
    /// One CreateRecordBuffer() call, in its own try/catch — an ordinary method call, not an
    /// iterator MoveNext(), so a throw for this ONE record cannot corrupt any other record's
    /// turn (see the file banner for why that distinction matters).
    /// </summary>
    private static void InsertAggregatePermissionSetRecord(object bcProvider, object store, object record, IReadOnlyDictionary<Guid, string> appNames)
    {
        var key = _apsRecordKeyField!.GetValue(record)
            ?? throw new InvalidOperationException("PermissionSetRecord.permissionSetKey was null");
        var appId = (Guid)_apsKeyAppIdProp!.GetValue(key)!;
        var appName = appId == Guid.Empty ? string.Empty : appNames.GetValueOrDefault(appId, string.Empty);

        object readOnlyBuffer;
        try
        {
            readOnlyBuffer = _apsCreateRecordBuffer!.Invoke(bcProvider, new object?[] { record, appName })!;
        }
        catch (TargetInvocationException tie) when (IsRoleIdTooLongForAggregateTable(tie.InnerException))
        {
            // BC's own "Aggregate Permission Set" Role ID column is Code[20] — narrower than
            // the Code[30] Role ID column on "Metadata Permission Set" it unions from
            // (confirmed from both tables' compiled SymbolReference.json, not assumed). A
            // permission set whose declared name/role id is 21-30 characters (System
            // Application ships one 22 characters long, "System Execute - Basic", with no
            // declared Properties at all — so no Caption, no explicit Assignable) is data
            // BC's own schema cannot represent in THIS table on any tier: CreateRecordBuffer
            // calls NavCode.ModifyLength(20) on the wider value, which constructs a fresh
            // NavCode(20, value), and that constructor throws rather than truncate (confirmed
            // by decompiling NavCode..ctor(int,string) and .ModifyLength — both real BC
            // behaviour, unmodified). We cannot silently truncate on BC's behalf here — that
            // would fabricate a Role ID BC itself never produces — so the only
            // value-preserving answer is to exclude this one row and let every OTHER row
            // still be inserted, the same way a row that cannot exist in a table's own schema
            // would never appear in a query result on any tier. A targeted `Get()` for this
            // exact role id would still correctly report "does not exist" afterwards.
            if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_AGGREGATE_PERMISSION_SET") == "1")
                Console.Error.WriteLine($"[aggregate-permission-set] excluded row: {tie.InnerException!.GetType().Name}: {tie.InnerException.Message}");
            return;
        }

        var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnlyBuffer });
        try
        {
            _aovTtdpInsert!.Invoke(store, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Same (Scope, App ID, Role ID) already present — faithful to a virtual table
            // where that triple is the primary key (e.g. a role BC's own union would also
            // report exactly once for both a System and Tenant declaration sharing a key,
            // which cannot happen since Scope is part of the key, but a defensive no-throw
            // here matches every sibling virtual-table populate function's own guard).
        }
    }

    /// <summary>App ID -> App Name for every app this table could possibly report a row for.</summary>
    private static Dictionary<Guid, string> BuildKnownAppNameIndex()
    {
        var names = new Dictionary<Guid, string>();
        foreach (var p in ParsedPermissionSets)
            if (p.AppId != Guid.Empty) names.TryAdd(p.AppId, p.AppName);
        foreach (var appPath in _bcAppPaths.ToArray())
        {
            BcAppSymbolCache.AppSymbols symbols;
            try { symbols = BcAppSymbolCache.Get(appPath); }
            catch { continue; }
            if (Guid.TryParse(symbols.AppId, out var appId) && appId != Guid.Empty)
                names.TryAdd(appId, symbols.AppName ?? string.Empty);
        }
        return names;
    }

    /// <summary>
    /// Fully enumerate a BC-returned <c>IEnumerable</c> into a list. Neither
    /// GetSystemPermissionSets() nor GetTenantPermissionSets() constructs a new NavCode or
    /// calls CreateRecordBuffer while doing so (both just cast/read fields already stored in
    /// an existing record), so unlike the combined GetAllItems() this cannot throw partway
    /// through for a per-row data reason — see the file banner.
    /// </summary>
    private static List<object> DrainToList(object enumerable)
    {
        var list = new List<object>();
        foreach (var item in (IEnumerable)enumerable) list.Add(item!);
        return list;
    }

    private static bool IsRoleIdTooLongForAggregateTable(Exception? ex)
        => ex?.GetType().Name == "NavNCLStringLengthExceededException";

    private static void EnsureAggregatePermissionSetReflection(NCLMetaTable metaTable)
    {
        if (_apsReflectionReady) return;

        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        _apsProviderType = ResolveType(rt + "AggregatePermissionSetDataProvider", rt + "AggregatePermissionSetDataProvider")
            ?? throw new InvalidOperationException("AggregatePermissionSetDataProvider type not found in Ncl");

        var tNavSession = ResolveType(rt + "NavSession", rt + "NavSession")
            ?? throw new InvalidOperationException("NavSession type not found");
        var tNclMetadata = ResolveType(rt + "NCLMetadata", rt + "NCLMetadata")
            ?? throw new InvalidOperationException("NCLMetadata type not found");
        var tNavValue = ResolveType(rt + "NavValue", "Microsoft.Dynamics.Nav.Types.NavValue")
            ?? throw new InvalidOperationException("NavValue type not found");
        var tNavCode = ResolveType(rt + "NavCode", "Microsoft.Dynamics.Nav.Types.NavCode")
            ?? throw new InvalidOperationException("NavCode type not found");
        var tPermissionSetKey = ResolveType(
            "Microsoft.Dynamics.Nav.Runtime.Permissions.PermissionSetKey",
            "Microsoft.Dynamics.Nav.Runtime.Permissions.PermissionSetKey")
            ?? throw new InvalidOperationException("PermissionSetKey type not found");
        var tPermissionSetRecord = _apsProviderType.GetNestedType("PermissionSetRecord", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("AggregatePermissionSetDataProvider.PermissionSetRecord type not found");

        _apsProviderCtor = _apsProviderType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { tNavSession, tNclMetadata }, modifiers: null)
            ?? throw new InvalidOperationException(
                "AggregatePermissionSetDataProvider(NavSession, NCLMetadata) ctor not found");

        _apsGetSystemPermissionSets = _apsProviderType.GetMethod("GetSystemPermissionSets",
            BindingFlags.NonPublic | BindingFlags.Instance, binder: null,
            types: new[] { tNavValue, tNavCode }, modifiers: null)
            ?? throw new InvalidOperationException(
                "AggregatePermissionSetDataProvider.GetSystemPermissionSets(NavValue, NavCode) not found");

        _apsGetTenantPermissionSets = _apsProviderType.GetMethod("GetTenantPermissionSets",
            BindingFlags.NonPublic | BindingFlags.Instance, binder: null,
            types: new[] { tNavValue, tNavCode }, modifiers: null)
            ?? throw new InvalidOperationException(
                "AggregatePermissionSetDataProvider.GetTenantPermissionSets(NavValue, NavCode) not found");

        _apsCreateRecordBuffer = _apsProviderType.GetMethod("CreateRecordBuffer",
            BindingFlags.NonPublic | BindingFlags.Instance, binder: null,
            types: new[] { tPermissionSetRecord, typeof(string) }, modifiers: null)
            ?? throw new InvalidOperationException(
                "AggregatePermissionSetDataProvider.CreateRecordBuffer(PermissionSetRecord, string) not found");

        _apsRecordKeyField = tPermissionSetRecord.GetField("permissionSetKey",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PermissionSetRecord.permissionSetKey field not found");

        _apsKeyAppIdProp = tPermissionSetKey.GetProperty("AppId",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("PermissionSetKey.AppId property not found");

        _apsSessionNclMetadata = tNavSession.GetProperty("NCLMetadata",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NavSession.NCLMetadata property not found");

        _apsReflectionReady = true;
    }
}
