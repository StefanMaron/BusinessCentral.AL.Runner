// RecordPatches.PermissionSystemTable — managed provider for the "Permission" system
// virtual table (2000000005), the rows that say what a permission set GRANTS.
//
// WHY THIS EXISTS
//   #2893 made BC's permission METADATA layer resolve — the app group's permission-set
//   inventory and one NCLMetaPermissionSet per set — and #3609 made a source-compiled set's
//   grants reach that layer with their object ids and masks already resolved, by reading
//   BC's own emitted <PermissionSet> document. Both landed, and table 2000000005 was still
//   empty, because nothing routed it: this file's dispatch arm did not exist, so the
//   runner's per-table store for 2000000005 was created and never populated.
//
//   Measured on the corpus at tip, BC 28.4, before this file: corpus codeunit 60604
//   "Test Perm. Set Grants" fails
//     PermissionSet_DeclaredTableDataGrant_IsListedAsAPermissionRow ("at least one Permission
//       row must exist") and
//     PermissionSet_TableDataGrant_CarriesTheReadWriteInsertDeleteMask
//       (NavCSideRecordNotFoundException, Role ID='ALTPERMISSIONSET' Object Type='Table Data'
//       Object ID='60000'),
//   while its third test passes VACUOUSLY — it asserts Object Type::Table yields no rows,
//   which an entirely empty table satisfies. That third test is the negative control for this
//   file: it only starts doing its job once rows exist, and it goes red if this file files a
//   tabledata grant under Object Type::Table.
//
// WHAT THIS DOES (faithful, managed, R2R-safe)
//   It drives BC's REAL, unmodified PermissionDataProvider rather than re-deriving what a
//   permission set grants. Three of its own members, in BC's own order:
//     GetPermissionSets(null)          -> GetMetadataPermissionSets, which reads the app group
//                                         inventory EnsurePermissionMetadataPopulated fills;
//     ComputePermissions(key)          -> PermissionSetGraphWalker.Walk + PermissionComposer,
//                                         i.e. BC's own include/exclude expansion and
//                                         extension merge;
//     GetRecord(key, definition)       -> the ReadOnlyRecordBuffer, with Object Type, Object ID
//                                         and the five R/I/M/D/X option columns laid out by
//                                         BC's own PermissionHelper.
//   So every column value in this table is computed by BC, from data the runner supplied to
//   BC's metadata layer. The runner contributes the inventory and the dispatch; it computes no
//   permission of its own. That is the audit justification .claude/rules/loud-failures.md
//   asks for: an in-scope caller reading this table observes what BC's provider answers,
//   because it IS what BC's provider answered.
//
// WHY ComputePermissions AND NOT GetPermissions
//   GetPermissions is a memo around ComputePermissions keyed on
//   session.Database.PermissionSetupMonitor.SetupVersion. The skeleton session has no
//   Database, so the memo's first line NREs before reaching the computation it caches.
//   Calling the computation directly is the same answer without the cache — measured: BC's own
//   PermissionDataProvider.ComputePermissions is `permissionComposer.Compose(
//   permissionSetGraphWalker.Walk(key), session)`, which reads no Database at all.
//
// REBUILT ON EVERY DISPATCH, AND WHAT THAT DOES NOT COVER
//   The store is cleared and rebuilt on every dispatch (ClearProviderInPlace), so a permission
//   set that becomes known between two Record variables cannot be missing and a stale row
//   cannot survive. What it does NOT cover is the second read on ONE already-open Record
//   variable: real BC's RecordImplementation.InitializeImpl resolves a NavRecord's DataAccess
//   wrapper at most once, so a later Get()/FindSet() on that same variable never re-dispatches
//   and reads whatever this file last stored. The Aggregate Permission Set sibling closes that
//   with a per-request redrive prepended to DataAccess.InternalTryGetByPrimaryKeyAsync (#2504),
//   which is the right shape here too — and it is NOT done here, deliberately, because the
//   registration lives in AlRunner/Infrastructure/NclCecilRewrite.Runtime.cs, held by another
//   open pull request while this was written. It costs nothing today: this table's rows derive
//   from the permission-set inventory, which is fixed for the lifetime of one runner
//   invocation, so a redrive on an already-open variable can only ever recompute the same rows.
//   It starts costing something the moment a permission set can be declared mid-run. #3697
//   tracks it.
//
// PRECOMPILED-DLL RESPECT
//   PermissionDataProvider, PermissionDataProviderBase, PermissionProvider, NCLMetadata,
//   NCLMetaTable, ReadOnlyRecordBuffer and TempTableDataProvider are runtime-engine types in
//   Ncl.dll. Nothing here constructs, rewrites or substitutes an AL-business-logic body.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// A store-wiring refusal on the Permission table. See RecordPatches.VirtualTableShapeGap.cs
    /// for the three-bucket classification; this is category (2), the same bucket the two
    /// sibling permission tables' own store-wiring refusals sit in.
    /// </summary>
    internal static RunnerOutOfScopeException PermissionSystemTableShapeGap(string detail)
        => VirtualTableShapeGap("Permission (system table 2000000005)", "permission-system-table", detail);

    /// <summary>
    /// A BC-internals read behind the Permission table (2000000005) that could not be
    /// performed — the same classification the sibling permission tables use, so a moved Ncl
    /// member is reported by name instead of arriving as an anonymous exception the
    /// <c>asserterror</c> seam absorbs.
    /// </summary>
    internal static BcShapeGapException PermissionSystemTableBcShapeGap(string member, string detail)
        => new("Permission (system table 2000000005)", member, detail);

    internal const int PermissionSystemTableId = 2000000005;

    private static bool _permTableReflectionReady;
    private static Type? _permTableProviderType;        // Microsoft.Dynamics.Nav.Runtime.PermissionDataProvider
    private static ConstructorInfo? _permTableProviderCtor;   // .ctor(NavSession, NCLMetadata, PermissionProvider)
    private static ConstructorInfo? _permTablePermProviderCtor; // PermissionProvider(NCLMetadata)
    private static MethodInfo? _permTableGetPermissionSets;    // protected IEnumerable<PermissionSetKey> GetPermissionSets(FilterExpression)
    private static MethodInfo? _permTableComputePermissions;   // protected IEnumerable<NavPermissionDefinition> ComputePermissions(PermissionSetKey)
    private static MethodInfo? _permTableGetRecord;            // protected ReadOnlyRecordBuffer GetRecord(PermissionSetKey, NavPermissionDefinition)
    private static PropertyInfo? _permTableSessionNclMetadata; // NavSession.NCLMetadata

    private static bool IsPermissionSystemTable(NCLMetaTable? table)
        => table != null && table.TableId == PermissionSystemTableId;

    /// <summary>
    /// Populate the in-memory store behind the Permission (2000000005) data access by driving
    /// BC's own <c>PermissionDataProvider</c> over every permission set the runner knows.
    /// </summary>
    private static void PopulatePermissionSystemTable(object dataAccess, NCLMetaTable metaTable, object session)
    {
        // The AllObj block resolved the shared Ncl helpers (buffer ctors,
        // TempTableDataProvider.Insert). Reuse them rather than resolving a second copy.
        EnsureAllObjReflection(metaTable);
        EnsurePermissionSystemTableReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var store = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw PermissionSystemTableShapeGap("data access has no in-memory provider");

        ClearProviderInPlace(store);

        // The moment the runner knows the permission-set inventory is complete and something
        // is asking about permissions. BC's GetMetadataPermissionSets below reads exactly the
        // app-group inventory this fills, so without it the provider enumerates nothing and
        // this table is empty for the same reason it was before this file existed.
        EnsurePermissionMetadataPopulated();

        var nclMetadata = _permTableSessionNclMetadata!.GetValue(session)
            ?? throw PermissionSystemTableShapeGap(
                "NavSession.NCLMetadata is null on the skeleton session, so BC's own "
                + "PermissionDataProvider cannot resolve the permission sets it reads");

        object bcProvider;
        List<object> permissionSetKeys;
        try
        {
            var permissionProvider = _permTablePermProviderCtor!.Invoke(new object?[] { nclMetadata });
            bcProvider = _permTableProviderCtor!.Invoke(new object?[] { session, nclMetadata, permissionProvider });
            // A null FilterExpression means "no App ID filter": BC's own GetMetadataPermissionSets
            // only evaluates the filter when appIdField is non-null, and this table declares no
            // App ID field, so the null is the same "every app" answer BC computes for an
            // unfiltered request rather than a stand-in for one.
            permissionSetKeys = DrainToList(_permTableGetPermissionSets!.Invoke(bcProvider, new object?[] { null })!);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable — satisfies the compiler's flow analysis
        }

        foreach (var key in permissionSetKeys)
            InsertPermissionRowsForSet(bcProvider, store, metaTable, key);
    }

    /// <summary>
    /// Every Permission row one permission set contributes: BC composes the set's effective
    /// permissions, and BC lays each one out as a record buffer.
    ///
    /// <para>The compose and the per-definition layout are separated deliberately, for the
    /// reason the Aggregate Permission Set sibling's banner sets out at length: a C# iterator
    /// that throws out of <c>MoveNext()</c> is terminally finished, so one bad item inside a
    /// single combined enumeration silently truncates every item after it. Draining the
    /// composition first and calling <c>GetRecord</c> per item afterwards makes a per-row
    /// failure an ordinary exception around one ordinary call.</para>
    /// </summary>
    private static void InsertPermissionRowsForSet(object bcProvider, object store, NCLMetaTable metaTable, object permissionSetKey)
    {
        List<object> definitions;
        try
        {
            // ComputePermissions, not GetPermissions — see the file banner: the latter's memo
            // reads session.Database, which the skeleton session does not have.
            definitions = DrainToList(_permTableComputePermissions!.Invoke(bcProvider, new object?[] { permissionSetKey })!);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }

        foreach (var definition in definitions)
        {
            object readOnlyBuffer;
            try
            {
                readOnlyBuffer = _permTableGetRecord!.Invoke(bcProvider, new[] { permissionSetKey, definition })!;
            }
            catch (TargetInvocationException tie) when (IsRoleIdTooLongForPermissionTable(tie.InnerException))
            {
                // "Role ID" on this table is Code[20], and GetRecord calls ModifyLength on the
                // wider value, whose NavCode constructor throws rather than truncate. A role id
                // BC's own schema cannot represent in THIS table is a row that exists on no
                // tier, so it is excluded rather than truncated — truncating would fabricate a
                // Role ID BC never emits. Same rule, same reason, as the Aggregate Permission
                // Set sibling's own exclusion.
                if (Environment.GetEnvironmentVariable("AL_RUNNER_TRACE_PERMISSION_TABLE") == "1")
                    Console.Error.WriteLine(
                        $"[permission-table] excluded row: {tie.InnerException!.GetType().Name}: {tie.InnerException.Message}");
                continue;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw; // unreachable
            }

            var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnlyBuffer });
            try
            {
                _aovTtdpInsert!.Invoke(store, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
            }
            catch (TargetInvocationException tie) when (
                tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
            {
                // The same (Role ID, Object Type, Object ID) already present. BC's own composer
                // unions grants across included sets, so two include edges reaching the same
                // object legitimately produce one row here — dropping the duplicate is what a
                // table whose primary key is that triple does on any tier.
            }
        }
    }

    private static bool IsRoleIdTooLongForPermissionTable(Exception? ex)
        => ex?.GetType().Name == "NavNCLStringLengthExceededException";

    private static void EnsurePermissionSystemTableReflection(NCLMetaTable metaTable)
    {
        if (_permTableReflectionReady) return;

        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        _permTableProviderType = ResolveType(rt + "PermissionDataProvider", rt + "PermissionDataProvider")
            ?? throw PermissionSystemTableBcShapeGap(
                "PermissionDataProvider",
                "type not found in Ncl — the Permission table cannot be populated");

        var tNavSession = ResolveType(rt + "NavSession", rt + "NavSession")
            ?? throw PermissionSystemTableBcShapeGap(
                "NavSession",
                "type not found in Ncl — the Permission table cannot be populated");
        var tNclMetadata = ResolveType(rt + "NCLMetadata", rt + "NCLMetadata")
            ?? throw PermissionSystemTableBcShapeGap(
                "NCLMetadata",
                "type not found in Ncl — the Permission table cannot be populated");
        var tPermissionProvider = ResolveType(
            rt + "Permissions.PermissionProvider", rt + "Permissions.PermissionProvider")
            ?? throw PermissionSystemTableBcShapeGap(
                "Permissions.PermissionProvider",
                "type not found in Ncl — the Permission table cannot be populated");
        var tPermissionSetKey = ResolveType(
            rt + "Permissions.PermissionSetKey", rt + "Permissions.PermissionSetKey")
            ?? throw PermissionSystemTableBcShapeGap(
                "Permissions.PermissionSetKey",
                "type not found in Ncl — the Permission table cannot be populated");
        // Permissions.NavPermissionDefinition, NOT Types.NavPermissionDefinition: the "Nav"
        // prefix reads like a Types-namespace value type and it is not one. Measured on Ncl
        // 28.1 — the only NavPermissionDefinition in the load chain is under
        // Microsoft.Dynamics.Nav.Runtime.Permissions, and the Types spelling resolved to
        // nothing, which is what this file's own refusal reported by name on its first run.
        var tPermissionDefinition = ResolveType(
            rt + "Permissions.NavPermissionDefinition", rt + "Permissions.NavPermissionDefinition")
            ?? throw PermissionSystemTableBcShapeGap(
                "Permissions.NavPermissionDefinition",
                "type not found in Ncl — the Permission table cannot be populated");
        var tFilterExpression = ResolveType(rt + "FilterExpression", rt + "FilterExpression")
            ?? throw PermissionSystemTableBcShapeGap(
                "FilterExpression",
                "type not found in Ncl — the Permission table cannot be populated");

        _permTablePermProviderCtor = tPermissionProvider.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { tNclMetadata }, modifiers: null)
            ?? throw PermissionSystemTableBcShapeGap(
                "PermissionProvider(NCLMetadata)",
                "constructor not found — the Permission table cannot be populated");

        _permTableProviderCtor = _permTableProviderType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { tNavSession, tNclMetadata, tPermissionProvider }, modifiers: null)
            ?? throw PermissionSystemTableBcShapeGap(
                "PermissionDataProvider(NavSession, NCLMetadata, PermissionProvider)",
                "constructor not found — the Permission table cannot be populated");

        // GetPermissionSets and ComputePermissions are declared abstract on
        // PermissionDataProviderBase and overridden here, so they are resolved on the DERIVED
        // type: the override is what a virtual dispatch would reach, and resolving the base
        // declaration would invoke through the same slot but state a member the reader would
        // have to follow one level to understand.
        _permTableGetPermissionSets = _permTableProviderType.GetMethod("GetPermissionSets",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: new[] { tFilterExpression }, modifiers: null)
            ?? throw PermissionSystemTableBcShapeGap(
                "PermissionDataProvider.GetPermissionSets(FilterExpression)",
                "method not found — the Permission table cannot be populated");

        _permTableComputePermissions = _permTableProviderType.GetMethod("ComputePermissions",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: new[] { tPermissionSetKey }, modifiers: null)
            ?? throw PermissionSystemTableBcShapeGap(
                "PermissionDataProvider.ComputePermissions(PermissionSetKey)",
                "method not found — the Permission table cannot be populated");

        _permTableGetRecord = _permTableProviderType.GetMethod("GetRecord",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, binder: null,
            types: new[] { tPermissionSetKey, tPermissionDefinition }, modifiers: null)
            ?? throw PermissionSystemTableBcShapeGap(
                "PermissionDataProvider.GetRecord(PermissionSetKey, NavPermissionDefinition)",
                "method not found — the Permission table cannot be populated");

        _permTableSessionNclMetadata = FindBcProperty(tNavSession, "NCLMetadata", out var nNclMetadata)
            ?? throw PermissionSystemTableBcShapeGap(
                "NavSession.NCLMetadata",
                BcPropertyGapDetail("NCLMetadata", nNclMetadata)
                + " — the Permission table cannot be populated");

        _permTableReflectionReady = true;
    }

}
