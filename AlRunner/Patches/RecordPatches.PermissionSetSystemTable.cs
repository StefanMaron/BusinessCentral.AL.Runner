// RecordPatches.PermissionSetSystemTable — managed provider for the legacy
// "Permission Set" system table (2000000004).
//
// WHY THIS EXISTS
//   Microsoft's own test libraries reach every named permission set through this table:
//
//     "Permissions Mock"(codeunit 131006).Assign(RoleID)
//       -> PermissionSet.Get(RoleID)                          // <- 2000000004
//       -> PermissionTestHelper.AddEffectivePermissionSet(...)
//
//   and "Library - Lower Permissions" (codeunit 132217) funnels SetO365Basic,
//   SetSalesDocsCreate, AddPermissionSet and its ~60 other PushPermissionSet wrappers
//   through it. On the runner 2000000004 routed to the same empty in-memory store as
//   every other table, so that Get raised "The Permission Set does not exist" and every
//   one of those entry points died (issue #3344). Its sibling "Aggregate Permission Set"
//   (2000000167) answered 170 rows in the same process at the same time, so the two
//   tables disagreed with each other in a way no service tier does.
//
// WHAT A ROW IS, ON A REAL TIER
//   2000000004 is declared as an ordinary (obsolete-pending) table, but a modern tier
//   does not read it from SQL. DataAccessSource.GetVirtualDataProvider routes it to
//   PermissionSetDataProvider whenever ServerUserSettings.UsePermissionSetsFromExtensions
//   is on — the default — and that provider is a MetadataPermissionSetDataProvider with
//   `includeNonAssignable: false`, computing its rows from permission-set METADATA:
//
//     field 1 "Role ID"  the permission set object's NAME, as a Code[20]
//     field 2 "Name"     NCLMetaPermissionSet.Caption, or the Role ID when it declares none
//     field 3 "Hash"     never written by the provider — blank on every metadata-served row
//
//   Measured on a real BC 28.4.53241.0 container with the test toolkit installed:
//   407 rows, Get('SUPER').Name = 'This role has all permissions.', Hash = ''.
//
// THE TWO PATHS ANSWER DIFFERENTLY, AND THAT IS BC, NOT AN ACCIDENT
//   MetadataPermissionSetDataProvider applies the assignable filter in exactly one of its
//   two entry points (decompiled from Ncl.dll, and measured on the tier afterwards):
//
//     GetAllItems      -> GetAllItemsInternal, whose loop is
//                         `if (includeNonAssignable || metaPermissionSet.Assignable)`
//                         — so WALKING the table lists assignable sets only.
//     TryGetByPrimaryKey -> resolves the Role ID against the app group's
//                         PermissionSetGroupObjectMetadataSummaries and never reads
//                         Assignable at all — so a keyed Get() finds a NON-assignable set.
//
//   Measured on the same container with a non-assignable fixture permission set: absent
//   from a FindSet walk, present through Get(). Upstream corpus codeunit 60291
//   "Test Permission Set Table" pins both halves.
//
//   That is why this file populates the store two different ways depending on which
//   request path is asking, rather than picking one and being wrong on the other:
//
//     find / count / exists  -> assignable sets only  (BC's GetAllItems)
//     Get() by primary key   -> every declared set    (BC's TryGetByPrimaryKey)
//
//   The default populate — the one GetDataAccessForTable runs on a record variable's
//   first touch — is the assignable-only shape, so a variable that is never used for a
//   keyed Get() never sees a non-assignable row.
//
// WHERE THE ROWS COME FROM HERE
//   EnumerateKnownPermissionSets() (RecordPatches.MetadataPermissionSetVirtualTable.cs) —
//   this run's own compiled AL source first, then each dependency .app's
//   SymbolReference.json. Exactly the inventory 2000000250 is built from, which is what
//   makes this table and the System scope of 2000000167 agree: the aggregate provider
//   reads 2000000250 through an AL NavRecord under a `SetFilter(Assignable, true)`, so
//   both views are the same rows filtered the same way.
//
// ROLE IDS TOO LONG FOR THIS TABLE'S OWN COLUMN
//   "Role ID" is Code[20] here and Code[30] on Metadata Permission Set, and BC's own
//   GetRecord calls roleId.ModifyLength(20) on the wider value — NavCode's ctor throws
//   rather than truncate. A permission set whose declared name exceeds 20 characters
//   (the System Application ships "System Execute - Basic", 22) is therefore data this
//   table's schema cannot represent on any tier, and it is excluded here rather than
//   silently truncated to a Role ID BC never produces. RecordPatches.AggregatePermission
//   SetVirtualTable.cs excludes the same rows for the same reason, which is also what
//   keeps the two row counts equal.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine types only (VirtualDataProvider, NCLMetaTable, NavValue,
//   ReadOnlyRecordBuffer, TempTableDataProvider). No AL business-logic body is touched:
//   codeunit 131006 and codeunit 132217 run unmodified, and it is the metadata under them
//   that changes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>
    /// Every refusal in this file, built in one place. See
    /// RecordPatches.VirtualTableShapeGap.cs for the three-bucket classification.
    /// </summary>
    /// <remarks>
    /// Category (2): one store-wiring refusal, on a table this file populates. An
    /// unresolvable role id is NOT a refusal — BC's own "The Permission Set does not
    /// exist" is the faithful answer there, and Microsoft's AL matches on it.
    /// </remarks>
    internal static RunnerOutOfScopeException PermissionSetSystemTableShapeGap(string detail)
        => VirtualTableShapeGap("Permission Set (system table 2000000004)", "permission-set-system-table", detail);

    internal const int PermissionSetSystemTableId = 2000000004;

    /// <summary>"Role ID" is Code[20] on table 2000000004 — narrower than the Code[30]
    /// column of the same name on Metadata Permission Set. Named rather than re-read per
    /// call because the Name column is a different length and must not be used for it.</summary>
    private const int PermissionSetRoleIdLength = 20;

    private static bool IsPermissionSetSystemTable(NCLMetaTable? table)
        => table != null && table.TableId == PermissionSetSystemTableId;

    /// <summary>
    /// Populate the in-memory store behind the Permission Set (2000000004) data access.
    /// </summary>
    /// <param name="includeNonAssignable">
    /// False for the walk/count/exists paths, mirroring BC's <c>GetAllItemsInternal</c>;
    /// true for the primary-key path, mirroring BC's <c>TryGetByPrimaryKey</c>, which
    /// never consults Assignable. See the file banner.
    /// </param>
    private static void PopulatePermissionSetSystemTable(object dataAccess, NCLMetaTable metaTable, bool includeNonAssignable)
    {
        // The AllObj block resolved the shared Ncl helpers (system-populated values, buffer
        // ctors, TempTableDataProvider.Insert, NavText/NavValue); the Metadata Permission Set
        // block resolved the NavCode ctor this file's Role ID column needs. Reuse both.
        EnsureAllObjReflection(metaTable);
        EnsureMetadataPermissionSetReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var store = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw PermissionSetSystemTableShapeGap("data access has no in-memory provider");

        // Rebuild the whole store on every touch rather than topping it up. Two reasons, and
        // the second is the one that matters here: the row set differs between the two request
        // paths (banner), so a store left carrying the primary-key path's extra non-assignable
        // rows would make a later walk list them — the exact divergence this file exists to
        // avoid. ClearProviderInPlace (RecordPatches.TransactionSnapshot.cs) only nulls the row
        // trees, not the "table" metadata field, so the provider stays usable afterward.
        ClearProviderInPlace(store);

        // #2893: the moment the runner knows the permission-set inventory is complete and
        // something is asking about permission sets. Idempotent and order-safe.
        EnsurePermissionMetadataPopulated();

        foreach (var (permissionSet, _, _) in EnumerateKnownPermissionSets())
        {
            if (!includeNonAssignable && !permissionSet.Assignable) continue;

            // A role id this table's own Code[20] column cannot hold is data BC's provider
            // cannot emit either — see the banner. Excluded, not truncated.
            if (permissionSet.Name.Length > PermissionSetRoleIdLength) continue;

            InsertPermissionSetSystemTableRow(store, metaTable, permissionSet);
        }
    }

    private static void InsertPermissionSetSystemTableRow(object store, NCLMetaTable metaTable, BcAppSymbolCache.PermissionSetSymbol permissionSet)
    {
        // Virtual-record identity: (tableId, permission-set object id, hash(role id)) — stable
        // per role so repeated handouts produce the same SystemId, exactly as the sibling
        // Metadata Permission Set rows do.
        var roleKey = StringComparer.OrdinalIgnoreCase.GetHashCode(permissionSet.Name) & 0x7fffffff;
        var values = _aovSystemValues!.Invoke(
            metaTable, PermissionSetSystemTableId, permissionSet.Id, roleKey, 0);

        foreach (var field in GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
        {
            var idx = field.FieldIndex;
            if (idx < 0 || idx >= values.Length) continue;
            if (values.GetValue(idx) != null) continue;   // BC already filled this slot

            values.SetValue(BuildPermissionSetSystemTableValue(field, permissionSet), idx);
        }

        var readOnly = _aovCtorReadOnlyBuffer!.Invoke(new object?[] { metaTable, values });
        var mutable = _aovCtorMutableBuffer!.Invoke(new object?[] { readOnly });
        try
        {
            _aovTtdpInsert!.Invoke(store, new object?[] { 0, mutable, _aovInsertOptionsNone, null });
        }
        catch (TargetInvocationException tie) when (
            tie.InnerException?.GetType().Name == "NavRecordAlreadyExistsException")
        {
            // Same Role ID already present — faithful to a table whose primary key is the
            // Role ID alone, and to BC's own summaries dictionary, which is keyed by it.
        }
    }

    /// <summary>
    /// One column of a Permission Set row. Columns are matched by the metatable's own FIELD
    /// NAME (case/space/hyphen-insensitive) so the mapping tracks whatever the System package
    /// in the resolved artifact declares, rather than hardcoded numbers. Anything we cannot
    /// answer truthfully gets BC's own default for that field.
    /// </summary>
    private static object? BuildPermissionSetSystemTableValue(NCLMetaField field, BcAppSymbolCache.PermissionSetSymbol permissionSet)
    {
        switch (NormalizeObjectTypeName(field.FieldName ?? string.Empty))
        {
            case "roleid":
                // Length-checked before we got here, so this NavCode ctor cannot throw.
                return RoleIdNavCode(permissionSet, field.FieldDefinedLength);
            case "name":
                // BC passes metaPermissionSet.Caption to GetRecord, and a permission set that
                // declares none is listed with its ROLE ID — the upper-case Code value —
                // rather than a blank. Measured on real BC 28.4: a caption-less Base
                // Application set lists Name = its own Role ID, and this suite's mixed-case
                // ALTPermissionSet lists ALTPERMISSIONSET. Same rule, same helper, as
                // Metadata Permission Set's Name column (#2474), so the two cannot disagree.
                return _aovNavTextCreateTruncated!.Invoke(null, new object?[]
                {
                    field.FieldDefinedLength,
                    string.IsNullOrEmpty(permissionSet.Caption)
                        ? RoleIdText(permissionSet)
                        : permissionSet.Caption
                });
            default:
                // "Hash" lands here, and blank is BC's own answer for it: PermissionSetData
                // Provider.GetRecord writes the Role ID and the Name and leaves every other
                // slot at the system-populated default. Measured: Get('SUPER').Hash = ''.
                return _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false });
        }
    }

    // ── The four request paths ──────────────────────────────────────────────────────────
    //
    // Real BC recomputes this table per request (VirtualAndTempTransactionalDataCache.TryFind
    // and TryGetByPrimaryKey return "miss" unconditionally, so every read reaches the provider
    // fresh), and the runner's store does not — RecordImplementation.InitializeImpl resolves a
    // NavRecord's DataAccess wrapper at most once, so without these guards only a variable's
    // FIRST touch would populate. The same four-path shape the Date and Field virtual tables
    // already carry (#2504, #2648, #2792, #3006): find, count, exists, get-by-primary-key.

    /// <summary>
    /// Shared by the three enumeration paths and by the primary-key path, differing only in
    /// which row set BC would compute for that path.
    /// </summary>
    private static void RepopulatePermissionSetSystemTableForRequest(object dataAccess, object request, bool includeNonAssignable)
    {
        // A `Record "Permission Set" temporary` holds exactly the rows AL inserted; repopulating
        // its private store would overwrite them. Same guard, same reason, as the Aggregate
        // Permission Set redrive (#2524).
        if (IsTemporaryRecordDataAccess(dataAccess)) return;
        if (FindRequestMetaApplicationObject(request) is not NCLMetaTable metaTable) return;
        PopulatePermissionSetSystemTable(dataAccess, metaTable, includeNonAssignable);
    }

    /// <summary>
    /// Prepended to DataAccess.CountAsync(CountCacheRequest). Record.Count() builds a
    /// CountCacheRequest, which the find guard never sees. For every table but this one it is
    /// a single int comparison.
    /// </summary>
    public static void DataAccess_PermissionSetSystemTableGuardForCount(object self, object request)
    {
        if (FindRequestTableId(request) != PermissionSetSystemTableId) return;
        RepopulatePermissionSetSystemTableForRequest(self, request, includeNonAssignable: false);
    }

    /// <summary>
    /// Prepended to DataAccess.ExistsAsync(ExistsCacheRequest) — the path
    /// RecordImplementation.IsEmptyAsync takes, which is neither the count path nor the find
    /// path (#3006).
    /// </summary>
    public static void DataAccess_PermissionSetSystemTableGuardForExists(object self, object request)
    {
        if (FindRequestTableId(request) != PermissionSetSystemTableId) return;
        RepopulatePermissionSetSystemTableForRequest(self, request, includeNonAssignable: false);
    }

    /// <summary>
    /// Prepended to DataAccess.InternalTryGetByPrimaryKeyAsync(PrimaryKeyCacheRequest) — the
    /// Get()-by-primary-key path. This is the one path that populates NON-assignable sets too,
    /// because BC's own TryGetByPrimaryKey resolves a Role ID without consulting Assignable.
    /// </summary>
    public static void DataAccess_PermissionSetSystemTableGuardForGet(object self, object request)
    {
        if (FindRequestTableId(request) != PermissionSetSystemTableId) return;
        RepopulatePermissionSetSystemTableForRequest(self, request, includeNonAssignable: true);
    }
}
