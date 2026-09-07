// RecordPatches.ObjectMetadataSystemTable — managed row source for the "Object Metadata"
// (2000000071) application-database system table. Issue #2519.
//
// 2000000071 is NOT a virtual table: it is one of the 43 ids in BC's own
// SystemTables.ApplicationDatabaseTables, a real SQL table that publishing writes into. The
// runner has no application database, so it synthesises one row per id in that list.
//
// The row set is a tier verdict, not an inference: corpus codeunit 61200
// (tests/al-language-onprem/record/TestObjectMetadataSystemTable.al, corpus PR #179) pins it at
// exactly 43 rows on all eight OnPrem legs, BC 27.0 through 28.4 — including that ObsoleteState
// Pending (2000000001) and Removed (2000000151) ids both get rows. Reading Microsoft's DELETE
// migration alone proves only a subset relation; an earlier version of this header rested the
// row set on it and claimed more than that evidence carried.
//
// Three columns are answered truthfully — "Object Type" (ordinal resolved BY NAME out of the
// metatable's own option string, never hardcoded), "Object ID", and "Emit Version"
// (NavEnvironment.Instance.EmitVersion, BC's own value for this process; it is the third
// primary-key field, so it must be real). The nine compiled-metadata payload columns REFUSE BY
// NAME on read (#2771) rather than answering blank, because 0 / '' / false / a 0-byte BLOB are
// legitimate values and a caller could not otherwise tell "no source" from "genuinely empty".
//
// The refusal is on the READ of the column, never at row-build time: throwing while building
// the row would take FindSet / FindLast / Count / IsEmpty with it, which is the bug #2519
// closed. And it fires only over rows the runner SYNTHESISED — --test-data can carry real
// published rows for this table, so ProviderHasAnyRow declines to synthesise and tells the
// guard, letting restored values through.
//
// ADDING A REFUSAL HERE: every one goes through ObjectMetadataShapeGap, and none of them is a
// scope boundary — this table is IN SCOPE and this file implements it, so the anchor is
// "not-yet-implemented" and the doc link is limitations.md, never scope.md (#2894). Two buckets,
// and the line between them is whether the runner obtained the information at all:
//   * the read SUCCEEDED and the answer was merely unwelcome (BC's list came back empty, the
//     artifact has no field 3, a skeleton singleton the RUNNER populates is null, the store
//     wiring handed no provider over) -> RunnerOutOfScopeException. An unwelcome answer is not
//     an unreadable one.
//   * the runner could not READ BC's internals (the primaryTree sites) -> BcShapeGapException.
// Sites 2/3/8/10 (a BC type or member that is not there) are the same family as the second
// bucket and are deliberately NOT converted here; that sweep spans this file and the 48 sites
// in RecordPatches.VirtualTableShapeGap.cs and is tracked separately.
//
// PRECOMPILED-DLL RESPECT: runtime-engine and Types-assembly members only (SystemTables,
// NavEnvironment, NCLMetaTable, NCLMetaField, NavValue, ReadOnlyRecordBuffer,
// TempTableDataProvider). No AL business-logic body is touched.
//
// row set, columns, the twelve preconditions, the Cloud/OnPrem target asymmetry, the two read
// seams and the expectation entries: docs/limitations.md#object-metadata-system-table
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int ObjectMetadataSystemTableId = 2000000071;

    private const int ObjectMetadataFieldObjectType = 3;
    private const int ObjectMetadataFieldObjectId = 6;
    private const int ObjectMetadataFieldEmitVersion = 37;

    /// <summary>
    /// The AL option member name (in <c>Object Metadata</c>."Object Type") whose ordinal every
    /// retained row carries. Microsoft's own cleanup migration deletes every row with
    /// <c>[Object Type] &lt;&gt; 1</c>, and ordinal 1 is <c>Table</c> in this table's option string.
    /// Resolved by NAME below so a future option-string change moves the ordinal with it.
    /// </summary>
    private const string ObjectMetadataRetainedObjectType = "Table";

    // Populated-once guard, per in-memory provider. The row set is a fixed BC-declared list,
    // so unlike AllObj there is nothing to top up on a later handout.
    //
    // The value is either _omsPopulateSucceeded or the ExceptionDispatchInfo of the refusal
    // that stopped it — see RunObjectMetadataPopulateOnce for why a failure has to be
    // remembered rather than forgotten.
    private static readonly ConditionalWeakTable<object, object> _omsPopulatedByProvider = new();

    private static readonly object _omsPopulateSucceeded = new();

    private static int[]? _omsApplicationDatabaseTableIds;
    private static int? _omsObjectTypeOrdinal;

    /// <summary>True if <paramref name="table"/> is the Object Metadata system table (2000000071).</summary>
    private static bool IsObjectMetadataSystemTable(NCLMetaTable? table)
        => table != null && table.TableId == ObjectMetadataSystemTableId;

    /// <summary>The API name every refusal in this file carries.</summary>
    internal const string ObjectMetadataApi = "Object Metadata (system table 2000000071)";

    /// <summary>
    /// The doc section that actually documents this table. NOT <c>docs/scope.md</c>: that file
    /// is the permanently-out-of-scope manifest and contains no object-metadata text at all
    /// (#2894).
    /// </summary>
    private const string ObjectMetadataDocLink = "docs/limitations.md#object-metadata-system-table";

    /// <summary>
    /// The one place a refusal for this table is built. See the file header for which bucket a
    /// new refusal belongs in, and .claude/rules/loud-failures.md for the rule.
    ///
    /// <para>Reason anchor <c>not-yet-implemented</c> is load-bearing, not cosmetic:
    /// ApplicationObjectBasePatches.IsPermanentOutOfScope traps a refusal into <c>false</c> for
    /// an AL <c>[TryFunction]</c> UNLESS the reason starts with it, so any other anchor makes a
    /// runner gap here read as a clean <c>if not TryX()</c> (#2894).</para>
    /// </summary>
    internal static RunnerOutOfScopeException ObjectMetadataShapeGap(string detail)
        => new(ObjectMetadataApi,
            "not-yet-implemented — object-metadata-system-table: " + detail,
            ObjectMetadataDocLink);

    /// <summary>
    /// Populate the in-memory store behind Object Metadata (2000000071) with one row per
    /// application-database system table, exactly the row set Microsoft's own
    /// CleanupObjectMetadataFromNonApplicationDatabaseTables migration retains.
    ///
    /// No-op when the store already holds any row — see the --test-data note in the file
    /// header: a restored backup's real rows are the better answer and must not be shadowed.
    /// </summary>
    private static void PopulateObjectMetadataSystemTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw ObjectMetadataShapeGap("data access has no in-memory provider");

        RunObjectMetadataPopulateOnce(provider, () =>
        {
            // --test-data (or an install baseline) already put real rows here — leave them alone.
            if (ProviderHasAnyRow(provider))
            {
                // ...and tell the no-source guard, or it refuses the nine payload columns over
                // rows that HAVE a source. This is the one branch that knows the difference:
                // everything below synthesises, everything here was restored. Without this the
                // refusal added for #2771 contradicts the precedence rule stated in this file's
                // header ("Real rows always win over synthesised ones") and fails loudly on
                // correct data. See RecordPatches.NoSourceColumns.cs.
                MarkObjectMetadataRowsAreReal();
                return;
            }

            var objectTypeOrdinal = EnsureObjectMetadataObjectTypeOrdinal(metaTable);
            var emitVersion = ReadNavEnvironmentEmitVersion();

            foreach (var tableId in EnumerateApplicationDatabaseTableIds())
            {
                InsertVirtualRow(provider, metaTable,
                    new object[] { ObjectMetadataSystemTableId, objectTypeOrdinal, tableId, emitVersion },
                    field => BuildObjectMetadataValue(field, objectTypeOrdinal, tableId, emitVersion));
            }
        });
    }

    /// <summary>
    /// Run <paramref name="populate"/> at most once per provider, and never let a populate
    /// that REFUSED be remembered as one that succeeded.
    ///
    /// <para>The claim is taken BEFORE the work, which is what makes the populate once-only
    /// under the concurrent handout in GetDataAccessForTableCore. Ten things after it can throw,
    /// and left alone each marked 2000000071 "populated" holding whatever it had when it failed,
    /// so every later access read an empty table with no diagnostic (#2786). Reachable from AL:
    /// <c>asserterror</c> is an unfiltered <c>catch</c> and can wrap this record-open path.</para>
    ///
    /// <para>A REFUSAL IS REPLAYED, NOT RETRIED. A retry after a part-way
    /// <c>InsertVirtualRow</c> finds rows already there, so <see cref="ProviderHasAnyRow"/>
    /// answers "already populated" and returns a silently PARTIAL table — the same bug in
    /// another hat. None of these failures is transient: BC's layout does not change mid-run.</para>
    /// </summary>
    private static void RunObjectMetadataPopulateOnce(object provider, Action populate)
    {
        // One populate per provider; the row set never grows within a run.
        lock (_omsPopulatedByProvider)
        {
            if (_omsPopulatedByProvider.TryGetValue(provider, out var prior))
            {
                // Throw() rather than `throw`: it preserves the original refusal's stack, so
                // the message still points at the member that actually moved.
                if (prior is ExceptionDispatchInfo refused) refused.Throw();
                return;
            }
            _omsPopulatedByProvider.Add(provider, _omsPopulateSucceeded);
        }

        try
        {
            populate();
        }
        catch (Exception ex)
        {
            lock (_omsPopulatedByProvider)
                _omsPopulatedByProvider.AddOrUpdate(provider, ExceptionDispatchInfo.Capture(ex));
            throw;
        }
    }

    /// <summary>
    /// One column of an Object Metadata row, matched by the metatable's own FIELD NUMBER —
    /// this table's columns are sparsely numbered (3, 6, 9, 15, 18, 27, 30, 33, 34, 35, 36, 37)
    /// and the three the runner answers are all key fields, so the number is the stable handle.
    /// Every other column is the compiled-metadata payload the runner does not have; see the
    /// declared-divergence section of this file's header.
    /// </summary>
    private static object? BuildObjectMetadataValue(
        NCLMetaField field, int objectTypeOrdinal, int tableId, int emitVersion)
        => field.FieldNo switch
        {
            ObjectMetadataFieldObjectType =>
                _aovNavOptionCreate!.Invoke(null, new object?[] { field.FieldOptionMetadata, objectTypeOrdinal }),
            ObjectMetadataFieldObjectId =>
                _aovNavIntegerCreate!.Invoke(null, new object?[] { tableId }),
            ObjectMetadataFieldEmitVersion =>
                _aovNavIntegerCreate!.Invoke(null, new object?[] { emitVersion }),
            _ => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
        };

    /// <summary>
    /// BC's own list of application-database system tables, read off
    /// <c>Microsoft.Dynamics.Nav.Types.SystemTables.ApplicationDatabaseTables</c>. Ascending, so
    /// a FindLast without a filter and one filtered to Table agree about which row is last.
    ///
    /// <para>A service tier has adjudicated this row set as an EQUALITY — 43 rows, corpus
    /// codeunit 61200 on eight OnPrem legs (corpus PR #179). If a tier ever disagrees, this
    /// method is the one place to filter.</para>
    /// </summary>
    private static int[] EnumerateApplicationDatabaseTableIds()
    {
        if (_omsApplicationDatabaseTableIds != null) return _omsApplicationDatabaseTableIds;

        var tSystemTables = ResolveType(
            "Microsoft.Dynamics.Nav.Runtime.SystemTables", "Microsoft.Dynamics.Nav.Types.SystemTables")
            ?? throw ObjectMetadataShapeGap(
                "Microsoft.Dynamics.Nav.Types.SystemTables not found, so BC's own "
                + "application-database table list cannot be read");

        var property = tSystemTables.GetProperty("ApplicationDatabaseTables",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw ObjectMetadataShapeGap("SystemTables.ApplicationDatabaseTables not found");

        var ids = new List<int>();
        if (property.GetValue(null) is IEnumerable values)
            foreach (var v in values)
                if (v is int id) ids.Add(id);

        if (ids.Count == 0)
            throw ObjectMetadataShapeGap(
                "SystemTables.ApplicationDatabaseTables is empty, so there is no row set to answer with");

        ids.Sort();
        _omsApplicationDatabaseTableIds = ids.ToArray();
        return _omsApplicationDatabaseTableIds;
    }

    /// <summary>
    /// The ordinal of "Table" in THIS artifact's Object Metadata "Object Type" option string,
    /// matched by name. Never hardcoded: the option string carries reserved blank slots and
    /// has gained members across versions, so the name is the stable handle and the ordinal
    /// is derived.
    /// </summary>
    private static int EnsureObjectMetadataObjectTypeOrdinal(NCLMetaTable metaTable)
    {
        if (_omsObjectTypeOrdinal is { } cached) return cached;

        var field = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => f.FieldNo == ObjectMetadataFieldObjectType)
            ?? throw ObjectMetadataShapeGap("metatable has no field 3 (\"Object Type\")");

        var optionString = field.FieldOptionMetadata?.OptionString
            ?? throw ObjectMetadataShapeGap("\"Object Type\" carries no option metadata");

        var wanted = NormalizeObjectTypeName(ObjectMetadataRetainedObjectType);
        var parts = optionString.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (NormalizeObjectTypeName(parts[i]) != wanted) continue;
            _omsObjectTypeOrdinal = i;
            return i;
        }

        throw ObjectMetadataShapeGap(
            $"\"Object Type\" option string ('{optionString}') has no "
            + $"'{ObjectMetadataRetainedObjectType}' member, so the ordinal every retained row carries "
            + "cannot be resolved");
    }

    /// <summary>
    /// BC's own emit version for this process (<c>NavEnvironment.Instance.EmitVersion</c>).
    /// It is the third primary-key field of Object Metadata, so it must be a real value read
    /// from BC rather than a number chosen here. On a skeleton NavEnvironment BC's own property
    /// reads 0, and 0 is then the truthful answer for this process — the rows are still unique
    /// and still ordered by (Object Type, Object ID).
    /// </summary>
    private static int ReadNavEnvironmentEmitVersion()
    {
        var tNavEnvironment = ResolveType(
            "Microsoft.Dynamics.Nav.Runtime.NavEnvironment", "Microsoft.Dynamics.Nav.Types.NavEnvironment")
            ?? throw ObjectMetadataShapeGap(
                "NavEnvironment not found, so BC's own emit version (the third primary-key field) "
                + "cannot be read");

        var instance = tNavEnvironment
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null)
            ?? throw ObjectMetadataShapeGap(
                "NavEnvironment.Instance is null, so BC's own emit version (the third primary-key field) "
                + "cannot be read");

        var emitVersion = tNavEnvironment
            .GetProperty("EmitVersion", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(instance)
            ?? throw ObjectMetadataShapeGap("NavEnvironment.EmitVersion not found");

        return (int)emitVersion;
    }

    /// <summary>
    /// True when the in-memory provider already holds at least one row. Reads the same
    /// <c>TempTableDataProvider.primaryTree</c> the stored-table census reads, where a null
    /// tree is BC's own representation of "no row was ever inserted".
    ///
    /// <para>A NULL tree and an ABSENT field are not the same answer (#2786). Null is BC's own
    /// "no row was ever inserted", so FALSE — synthesise — is right. Absent means BC's layout
    /// moved and the runner cannot see what the store holds; answering FALSE would silently
    /// shadow whatever --test-data restored, so it refuses instead, naming the member.</para>
    ///
    /// <para>The type is <see cref="BcShapeGapException"/>, not
    /// <see cref="RunnerOutOfScopeException"/> (#2946): a shape gap cannot be absorbed by an
    /// <c>expect-oos</c> entry (it is a property of which BC build is on disk, so it can differ
    /// per leg) and it tears through AL's <c>asserterror</c>, which matters because on real BC
    /// this record-open SUCCEEDS.</para>
    ///
    /// <para>Resolution goes through <see cref="BcShape"/> / <see cref="PrivateMemberLookup"/>,
    /// never a plain <c>GetField</c>: <c>primaryTree</c> is private on
    /// <c>TempTableDataProvider</c> and <c>GetField(NonPublic)</c> on a DERIVED type does not
    /// return a base class's private fields — BC's <c>CrmTestDataProvider</c> derives from it,
    /// and reading its inherited field as "absent" would refuse over a readable store (#2725).</para>
    /// </summary>
    private static bool ProviderHasAnyRow(object provider)
    {
        const string detail =
            "the runner cannot tell a store BC never inserted into from one --test-data already "
            + "filled, and synthesising rows would silently shadow the restored ones";

        var field = BcShape.RequiredField(
            provider.GetType(), "primaryTree", ObjectMetadataApi, detail);

        // A null tree is BC's own "no row was ever inserted": nothing to shadow, synthesise.
        var tree = field.GetValue(provider);
        if (tree == null) return false;

        var rows = BcShape.RequiredEnumerable(
            tree, $"{provider.GetType().Name}.primaryTree", ObjectMetadataApi, detail);

        // Short-circuit: one row is the whole answer, and a restored backup can be large.
        foreach (var _ in rows) return true;
        return false;
    }
}
