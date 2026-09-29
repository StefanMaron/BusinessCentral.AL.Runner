// RecordPatches.PermissionSystemTable — managed provider for the three
// PermissionDataProviderBase tables, the rows that say what a permission set GRANTS:
//   2000000005 "Permission"           -> BC's PermissionDataProvider (composed, assignable sets)
//   2000000251 "Metadata Permission"  -> BC's MetadataPermissionDataProvider (declared, every set)
//   2000000254 "Expanded Permission"  -> BC's ExpandedPermissionDataProvider (composed, every set,
//                                        plus Tenant Permission Set rows)
// All three are driven through the same three BC members below; each provider's own overrides
// decide which sets it lists, whether it expands includes, and how a row is laid out (#2910).
// Corpus codeunits 60604 (Permission) and 67945 (Metadata/Expanded Permission) adjudicate them.
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
// REBUILT ON EVERY DISPATCH AND ON EVERY REQUEST
//   The store is cleared and rebuilt when a Record variable resolves its DataAccess and again
//   on each find/count/exists/get request (the guards at the end of this file, #3705), because
//   real BC computes these tables per request and "Expanded Permission" lists tenant sets AL
//   can insert mid-run.
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
    internal const int PermissionSystemTableId = 2000000005;
    internal const int MetadataPermissionSystemTableId = 2000000251;
    internal const int ExpandedPermissionSystemTableId = 2000000254;

    /// <summary>One PermissionDataProviderBase table and the BC provider that serves it.</summary>
    internal sealed record PermissionFamilyTable(
        int TableId, string Api, string Surface, string ProviderTypeName, bool TakesPermissionProvider);

    internal static readonly PermissionFamilyTable[] PermissionFamilyTables =
    {
        new(PermissionSystemTableId, "Permission (system table 2000000005)",
            "permission-system-table", "PermissionDataProvider", TakesPermissionProvider: true),
        new(MetadataPermissionSystemTableId, "Metadata Permission (system table 2000000251)",
            "metadata-permission-system-table", "MetadataPermissionDataProvider", TakesPermissionProvider: false),
        new(ExpandedPermissionSystemTableId, "Expanded Permission (system table 2000000254)",
            "expanded-permission-system-table", "ExpandedPermissionDataProvider", TakesPermissionProvider: true),
    };

    private sealed class PermissionFamilyBinding
    {
        public required ConstructorInfo Ctor;
        public required MethodInfo GetPermissionSets;   // protected IEnumerable<PermissionSetKey> GetPermissionSets(FilterExpression)
        public required MethodInfo ComputePermissions;  // protected IEnumerable<NavPermissionDefinition> ComputePermissions(PermissionSetKey)
        public required MethodInfo GetRecord;           // protected ReadOnlyRecordBuffer GetRecord(PermissionSetKey, NavPermissionDefinition)
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, PermissionFamilyBinding> _permFamilyBindings = new();

    private static bool _permTableReflectionReady;
    private static Type? _permTableNavSessionType;
    private static Type? _permTableNclMetadataType;
    private static Type? _permTablePermissionProviderType;
    private static Type? _permTablePermissionSetKeyType;
    private static Type? _permTablePermissionDefinitionType;
    private static Type? _permTableFilterExpressionType;
    private static ConstructorInfo? _permTablePermProviderCtor; // PermissionProvider(NCLMetadata)
    private static PropertyInfo? _permTableSessionNclMetadata; // NavSession.NCLMetadata
    private static object? _permTableAllAppsFilter;            // FilterExpression.BooleanConstant(true)

    internal static PermissionFamilyTable? PermissionFamilyTableFor(NCLMetaTable? table)
    {
        if (table == null) return null;
        foreach (var t in PermissionFamilyTables)
            if (t.TableId == table.TableId) return t;
        return null;
    }

    private static RunnerOutOfScopeException PermissionFamilyShapeGap(PermissionFamilyTable t, string detail)
        => VirtualTableShapeGap(t.Api, t.Surface, detail);

    private static BcShapeGapException PermissionFamilyBcShapeGap(PermissionFamilyTable t, string member, string detail)
        => new(t.Api, member, detail);

    /// <summary>
    /// Populate the in-memory store behind one PermissionDataProviderBase table by driving
    /// that table's own BC provider over every permission set it lists.
    /// </summary>
    internal static void PopulatePermissionFamilyTable(PermissionFamilyTable t, object dataAccess, NCLMetaTable metaTable, object session)
    {
        // The AllObj block resolved the shared Ncl helpers (buffer ctors,
        // TempTableDataProvider.Insert). Reuse them rather than resolving a second copy.
        EnsureAllObjReflection(metaTable);
        EnsurePermissionSystemTableReflection(t);
        var binding = EnsurePermissionFamilyBinding(t);
        EnsureDataAccessProviderReflection(dataAccess);

        var store = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw PermissionFamilyShapeGap(t, "data access has no in-memory provider");

        ClearProviderInPlace(store);

        // The moment the runner knows the permission-set inventory is complete and something
        // is asking about permissions. BC's GetMetadataPermissionSets below reads exactly the
        // app-group inventory this fills, so without it the provider enumerates nothing and
        // this table is empty for the same reason it was before this file existed.
        EnsurePermissionMetadataPopulated();

        var nclMetadata = _permTableSessionNclMetadata!.GetValue(session)
            ?? throw PermissionFamilyShapeGap(t,
                "NavSession.NCLMetadata is null on the skeleton session, so BC's own "
                + t.ProviderTypeName + " cannot resolve the permission sets it reads");

        object bcProvider;
        List<object> permissionSetKeys;
        try
        {
            var args = t.TakesPermissionProvider
                ? new object?[] { session, nclMetadata, _permTablePermProviderCtor!.Invoke(new object?[] { nclMetadata }) }
                : new object?[] { session, nclMetadata };
            bcProvider = binding.Ctor.Invoke(args);
            // The App ID filter is BC's own constant-true expression: the unfiltered request.
            // GetMetadataPermissionSets EVALUATES it on a table with an App ID field (251, 254),
            // so a null here NREs inside BC's lambda; 2000000005 has no App ID field and ignores it.
            permissionSetKeys = DrainToList(binding.GetPermissionSets.Invoke(bcProvider, new[] { _permTableAllAppsFilter })!);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable — satisfies the compiler's flow analysis
        }

        foreach (var key in permissionSetKeys)
            InsertPermissionRowsForSet(binding, bcProvider, store, key);
    }

    /// <summary>
    /// Every row one permission set contributes: BC computes the set's permissions (composed or
    /// declared, per the provider), and BC lays each one out as a record buffer.
    ///
    /// <para>The compute and the per-definition layout are separated deliberately, for the
    /// reason the Aggregate Permission Set sibling's banner sets out at length: a C# iterator
    /// that throws out of <c>MoveNext()</c> is terminally finished, so one bad item inside a
    /// single combined enumeration silently truncates every item after it. Draining the
    /// composition first and calling <c>GetRecord</c> per item afterwards makes a per-row
    /// failure an ordinary exception around one ordinary call.</para>
    /// </summary>
    private static void InsertPermissionRowsForSet(PermissionFamilyBinding binding, object bcProvider, object store, object permissionSetKey)
    {
        List<object> definitions;
        try
        {
            // ComputePermissions, not GetPermissions — see the file banner: the latter's memo
            // reads session.Database, which the skeleton session does not have.
            definitions = DrainToList(binding.ComputePermissions.Invoke(bcProvider, new object?[] { permissionSetKey })!);
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
                readOnlyBuffer = binding.GetRecord.Invoke(bcProvider, new[] { permissionSetKey, definition })!;
            }
            catch (TargetInvocationException tie) when (IsRoleIdTooLongForPermissionTable(tie.InnerException))
            {
                // GetRecord calls ModifyLength on the role id to the table's own "Role ID"
                // length, whose NavCode constructor throws rather than truncate. A role id
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
                // The same primary key already present. BC's own composer unions grants across
                // included sets, so two include edges reaching the same object legitimately
                // produce one row here — dropping the duplicate is what a table whose primary
                // key is that tuple does on any tier.
            }
        }
    }

    // ── Per-request redrive (#3705) ─────────────────────────────────────────────────────
    // Real BC computes these tables per request; the runner's store is filled when a Record
    // variable first resolves its DataAccess, and RecordImplementation.InitializeImpl does
    // that at most once. "Expanded Permission" lists Tenant Permission Set rows, which AL can
    // insert mid-run, so without these guards an open variable answers from a stale store
    // (corpus 67945, ExpandedPermission_OpenVariable_SeesATenantSetInsertedAfterItsFirstRead).
    // Same four paths as the Aggregate Permission Set redrive (#2504): find, count, exists, get.

    internal static bool IsPermissionFamilyTableId(int tableId)
        => tableId == PermissionSystemTableId
           || tableId == MetadataPermissionSystemTableId
           || tableId == ExpandedPermissionSystemTableId;

    /// <summary>Rebuild the store for THIS request, from the request's own table.</summary>
    internal static void RedrivePermissionFamilyForRequest(object dataAccess, object request)
    {
        // A temporary record holds exactly the rows AL inserted; a redrive would overwrite them
        // (the Aggregate sibling's #2524).
        if (IsTemporaryRecordDataAccess(dataAccess)) return;
        if (FindRequestMetaApplicationObject(request) is not NCLMetaTable metaTable) return;
        if (PermissionFamilyTableFor(metaTable) is not { } t) return;

        EnsureAggregatePermissionSetLiveGuardReflection(dataAccess);
        var session = _apsDataAccessSession!.GetValue(dataAccess)
            ?? throw PermissionFamilyShapeGap(t,
                "the DataAccess carries no session, so the table cannot be recomputed for this "
                + "request and would answer from rows computed for an earlier one");
        PopulatePermissionFamilyTable(t, dataAccess, metaTable, session);
    }

    /// <summary>Prepended to DataAccess.CountAsync(CountCacheRequest).</summary>
    public static void DataAccess_PermissionFamilyGuardForCount(object self, object request)
    {
        if (!IsPermissionFamilyTableId(FindRequestTableId(request))) return;
        RedrivePermissionFamilyForRequest(self, request);
    }

    /// <summary>Prepended to DataAccess.ExistsAsync(ExistsCacheRequest) — the IsEmpty() path.</summary>
    public static void DataAccess_PermissionFamilyGuardForExists(object self, object request)
    {
        if (!IsPermissionFamilyTableId(FindRequestTableId(request))) return;
        RedrivePermissionFamilyForRequest(self, request);
    }

    /// <summary>Prepended to DataAccess.InternalTryGetByPrimaryKeyAsync(PrimaryKeyCacheRequest).</summary>
    public static void DataAccess_PermissionFamilyGuardForGet(object self, object request)
    {
        if (!IsPermissionFamilyTableId(FindRequestTableId(request))) return;
        RedrivePermissionFamilyForRequest(self, request);
    }

    private static bool IsRoleIdTooLongForPermissionTable(Exception? ex)
        => ex?.GetType().Name == "NavNCLStringLengthExceededException";

    private static PermissionFamilyBinding EnsurePermissionFamilyBinding(PermissionFamilyTable t)
    {
        if (_permFamilyBindings.TryGetValue(t.TableId, out var cached)) return cached;

        const string rt = "Microsoft.Dynamics.Nav.Runtime.";
        var providerType = ResolveType(rt + t.ProviderTypeName, rt + t.ProviderTypeName)
            ?? throw PermissionFamilyBcShapeGap(t, t.ProviderTypeName,
                "type not found in Ncl — the table cannot be populated");

        var ctorTypes = t.TakesPermissionProvider
            ? new[] { _permTableNavSessionType!, _permTableNclMetadataType!, _permTablePermissionProviderType! }
            : new[] { _permTableNavSessionType!, _permTableNclMetadataType! };
        var ctor = providerType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: ctorTypes, modifiers: null)
            ?? throw PermissionFamilyBcShapeGap(t,
                t.ProviderTypeName + (t.TakesPermissionProvider
                    ? "(NavSession, NCLMetadata, PermissionProvider)"
                    : "(NavSession, NCLMetadata)"),
                "constructor not found — the table cannot be populated");

        // GetPermissionSets, ComputePermissions and GetRecord are declared abstract on
        // PermissionDataProviderBase and overridden per provider, so they are resolved on the
        // DERIVED type: the override is what decides which table's question is answered.
        MethodInfo Bind(string name, Type[] types, string display)
            => providerType.GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, binder: null,
                types: types, modifiers: null)
               ?? throw PermissionFamilyBcShapeGap(t, t.ProviderTypeName + "." + display,
                   "method not found — the table cannot be populated");

        var binding = new PermissionFamilyBinding
        {
            Ctor = ctor,
            GetPermissionSets = Bind("GetPermissionSets",
                new[] { _permTableFilterExpressionType! }, "GetPermissionSets(FilterExpression)"),
            ComputePermissions = Bind("ComputePermissions",
                new[] { _permTablePermissionSetKeyType! }, "ComputePermissions(PermissionSetKey)"),
            GetRecord = Bind("GetRecord",
                new[] { _permTablePermissionSetKeyType!, _permTablePermissionDefinitionType! },
                "GetRecord(PermissionSetKey, NavPermissionDefinition)"),
        };
        return _permFamilyBindings.GetOrAdd(t.TableId, binding);
    }

    private static void EnsurePermissionSystemTableReflection(PermissionFamilyTable t)
    {
        if (_permTableReflectionReady) return;

        const string rt = "Microsoft.Dynamics.Nav.Runtime.";

        Type Resolve(string name)
            => ResolveType(rt + name, rt + name)
               ?? throw PermissionFamilyBcShapeGap(t, name,
                   "type not found in Ncl — the table cannot be populated");

        _permTableNavSessionType = Resolve("NavSession");
        _permTableNclMetadataType = Resolve("NCLMetadata");
        _permTablePermissionProviderType = Resolve("Permissions.PermissionProvider");
        _permTablePermissionSetKeyType = Resolve("Permissions.PermissionSetKey");
        // Permissions.NavPermissionDefinition, NOT Types.NavPermissionDefinition: the "Nav"
        // prefix reads like a Types-namespace value type and it is not one (measured on Ncl 28.1).
        _permTablePermissionDefinitionType = Resolve("Permissions.NavPermissionDefinition");
        _permTableFilterExpressionType = Resolve("FilterExpression");

        _permTablePermProviderCtor = _permTablePermissionProviderType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: new[] { _permTableNclMetadataType }, modifiers: null)
            ?? throw PermissionFamilyBcShapeGap(t,
                "PermissionProvider(NCLMetadata)",
                "constructor not found — the table cannot be populated");

        _permTableSessionNclMetadata = FindBcProperty(_permTableNavSessionType, "NCLMetadata", out var nNclMetadata)
            ?? throw PermissionFamilyBcShapeGap(t,
                "NavSession.NCLMetadata",
                BcPropertyGapDetail("NCLMetadata", nNclMetadata)
                + " — the table cannot be populated");

        var booleanConstant = _permTableFilterExpressionType.GetMethod("BooleanConstant",
            BindingFlags.Public | BindingFlags.Static, binder: null, types: new[] { typeof(bool) }, modifiers: null)
            ?? throw PermissionFamilyBcShapeGap(t,
                "FilterExpression.BooleanConstant(bool)",
                "method not found — the table's App ID filter cannot be built");
        _permTableAllAppsFilter = booleanConstant.Invoke(null, new object[] { true })
            ?? throw PermissionFamilyBcShapeGap(t,
                "FilterExpression.BooleanConstant(true)", "answered null");

        _permTableReflectionReady = true;
    }

}
