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
//   what belongs in each scope), we construct BC's REAL, unmodified
//   AggregatePermissionSetDataProvider for the skeleton session and NCLMetadata and drive
//   its own GetAllItems() by reflection. Its System-scope query reads table 2000000250
//   (already served faithfully by RecordPatches.MetadataPermissionSetVirtualTable.cs,
//   issue #2313/#2330) through a real NavRecord — the SAME lazy-populate dispatch this
//   table itself goes through — and its Tenant-scope query reads table 2000000165
//   ("Tenant Permission Set"), a normal, already-working table. Both scopes are
//   therefore backed by data this runner already answers truthfully; we only need to
//   union them the way BC's own class does, which its own code already does for us. Each
//   ReadOnlyRecordBuffer it yields is BC's own CreateRecordBuffer output — every column
//   already laid out the way BC lays it out (Scope option value, App ID, Role ID, Name,
//   App Name, plus BC's system slots) — so we simply insert it into our in-memory store,
//   with no field-by-field reconstruction of our own.
//
// PRECOMPILED-DLL RESPECT
//   AggregatePermissionSetDataProvider, EagerVirtualDataProvider, NCLMetadata, NCLMetaTable,
//   NavValue, ReadOnlyRecordBuffer and TempTableDataProvider are all runtime-engine types
//   (Ncl.dll) — none of this touches an AL-business-logic DLL body. Codeunit134614 (Tests-
//   SINGLESERVER, Microsoft's own AL) runs completely unmodified; only the metadata under it
//   changes, exactly as RecordPatches.MetadataPermissionSetVirtualTable.cs does for its
//   sibling table.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int AggregatePermissionSetVirtualTableId = 2000000167;

    // Per in-memory-provider guard: GetAllItems() is an eager, one-shot enumeration of the
    // whole union, so repeated population attempts (one per lazy touch of the table) must
    // only ever insert once per provider instance — the same idempotency shape as
    // RecordPatches.MetadataPermissionSetVirtualTable.cs's own `_mpsPopulatedByProvider`.
    private static readonly ConditionalWeakTable<object, object> _apsPopulatedByProvider = new();

    private static bool _apsReflectionReady;
    private static Type? _apsProviderType;                 // Microsoft.Dynamics.Nav.Runtime.AggregatePermissionSetDataProvider
    private static ConstructorInfo? _apsProviderCtor;       // .ctor(NavSession, NCLMetadata)
    private static MethodInfo? _apsGetAllItems;              // protected override IEnumerable<ReadOnlyRecordBuffer> GetAllItems(out bool)
    private static PropertyInfo? _apsSessionNclMetadata;     // NavSession.NCLMetadata

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

        // One materialisation per provider instance — GetAllItems() is eager and yields the
        // whole union every call, so a second call would try to insert the same rows again.
        if (_apsPopulatedByProvider.TryGetValue(store, out _)) return;
        _apsPopulatedByProvider.AddOrUpdate(store, store);

        var nclMetadata = _apsSessionNclMetadata!.GetValue(session)
            ?? throw new RunnerOutOfScopeException(
                "Aggregate Permission Set (virtual table 2000000167)",
                "aggregate-permission-set-virtual-table — NavSession.NCLMetadata is null on the "
                + "skeleton session, so BC's own AggregatePermissionSetDataProvider cannot resolve "
                + "the System/Tenant Permission Set tables it unions; see docs/scope.md");

        object bcProvider;
        IEnumerable rows;
        try
        {
            bcProvider = _apsProviderCtor!.Invoke(new object?[] { session, nclMetadata });
            var args = new object?[] { null };
            rows = (IEnumerable)_apsGetAllItems!.Invoke(bcProvider, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable — satisfies the compiler's flow analysis
        }

        foreach (var readOnlyBuffer in rows)
        {
            var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnlyBuffer });
            try
            {
                _aovTtdpInsert!.Invoke(store, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
            }
            catch (TargetInvocationException tie) when (
                tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
            {
                // Same (Scope, App ID, Role ID) already present — faithful to a virtual
                // table where that triple is the primary key (e.g. a role BC's own union
                // would also report exactly once for both a System and Tenant declaration
                // sharing a key, which cannot happen since Scope is part of the key, but a
                // defensive no-throw here matches every sibling virtual-table populate
                // function's own guard).
            }
        }
    }

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

        _apsProviderCtor = _apsProviderType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { tNavSession, tNclMetadata }, modifiers: null)
            ?? throw new InvalidOperationException(
                "AggregatePermissionSetDataProvider(NavSession, NCLMetadata) ctor not found");

        _apsGetAllItems = _apsProviderType.GetMethod("GetAllItems",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "AggregatePermissionSetDataProvider.GetAllItems(out bool) not found");

        _apsSessionNclMetadata = tNavSession.GetProperty("NCLMetadata",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("NavSession.NCLMetadata property not found");

        _apsReflectionReady = true;
    }
}
