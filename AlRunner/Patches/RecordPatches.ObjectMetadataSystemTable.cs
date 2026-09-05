// RecordPatches.ObjectMetadataSystemTable — managed row source for the "Object Metadata"
// (2000000071) application-database system table.
//
// WHY THIS EXISTS (issue #2519)
//   Object Metadata routed to the same empty in-memory store as every other table the runner
//   has no rows for, so:
//
//       ObjectMetadata.SetRange("Object Type", ..."Object Type"::Table);
//       ObjectMetadata.FindLast();   ->  "There is no Object Metadata within the filter."
//
//   Microsoft's own Tests-SINGLESERVER bucket hits this in
//   Codeunit136608.VerifyValidatePackageCodeunitFailed, which reads one row purely to pick an
//   object id that exists and never gets past the FindLast.
//
// ── WHAT REAL BC HAS IN THIS TABLE (and what it does NOT) ────────────────────────────────
//   2000000071 is NOT a virtual table. It does not appear in
//   Microsoft.Dynamics.Nav.Types.SystemTables.VirtualTables, it has no DataProvider in
//   Ncl.dll, and Microsoft.Dynamics.Nav.Runtime.Apps.ObjectMetadataStorage reads it with plain
//   SQL against the APPLICATION database. It is one of the 43 ids in
//   SystemTables.ApplicationDatabaseTables — it stores itself.
//
//   Its content is NOT "one row per compiled object". Microsoft says so twice, in code we ship
//   with every artifact:
//
//   1. The table's own AL declaration, System.app
//      src/Application Database Tables/ObjectMetadata.Table.al:
//
//        /// The [Object Metadata] table contains the metadata information for system tables
//        /// with a SQL schema.
//        /// This table originally contained metadata for all objects, but this role is now
//        /// taken by [Application Object Metadata]. Later on, it only contained the metadata
//        /// for all system objects, before being now limited to Application database tables.
//        /// If the list of system objects needs to be accessed, the [System Object] table
//        /// should be used instead.
//
//   2. Microsoft.BusinessCentral.SystemApp.CleanupObjectMetadataFromNonApplicationDatabaseTables,
//      a [DbMigration(DatabaseType.Application, PreUpgrade, Order = 10)] whose whole body is:
//
//        DELETE FROM [dbo].[Object Metadata]
//        WHERE [Object Type] <> 1 OR [Object ID] NOT IN (<SystemTables.ApplicationDatabaseTables>)
//
//      Object Type 1 is "Table". So after any BC upgrade the retained rows are EXACTLY
//      "Object Type = Table, over the application-database system table ids", and nothing else.
//
//   That is the row set this file synthesises, read out of BC's OWN
//   SystemTables.ApplicationDatabaseTables rather than a second copy of the list that would
//   drift — the same technique the Feature Key provider uses for BC's hardcoded feature list.
//   Issue #2519's own "expected behavior" note said one row per COMPILED object; that is what
//   the table held in NAV and up to early BC, and the two Microsoft sources above are why this
//   implementation does not do that. AllObj / System Object (2000000029) are where the full
//   object inventory lives, and the runner already answers AllObj.
//
// ── THE COLUMNS, AND WHICH ONES ARE A DECLARED DIVERGENCE ────────────────────────────────
//   Answered truthfully:
//     "Object Type"    — option ordinal for "Table", resolved BY NAME out of the metatable's
//                        own option string (never a hardcoded ordinal).
//     "Object ID"      — the application-database system table id.
//     "Emit Version"   — NavEnvironment.Instance.EmitVersion, BC's own value for THIS process.
//                        It is the third primary-key field, so it has to be a real value, not
//                        an invented one.
//
//   NOT answered — the compiled-metadata payload:
//     Metadata, "User Code", "User AL Code", "Symbol Reference" (BLOBs), "Metadata Version",
//     Hash, "Object Subtype", "Has Subscribers", "Schema Hash".
//
//   On a real tier those carry the output of publishing the system app into the application
//   database. The runner never publishes anything into a database and has no such payload, so
//   they get BC's own NavValue.GetDefaultNavValue — an empty BLOB, 0, the empty string.
//
//   THIS IS A DECLARED DIVERGENCE, NOT A FAITHFUL SUBSTITUTION, and it is recorded as one in
//   docs/limitations.md and asserted by tests/runner-extras/object-metadata-system-table so it
//   cannot change quietly. Making a CalcFields of those BLOBs refuse by name (which is what
//   .claude/rules/loud-failures.md would prefer) needs a per-field blob-read seam on the shared
//   TempTableDataProvider path that does not exist yet; issue #2771 tracks it. Until then the
//   choice is between an empty payload and no row at all, and no row at all is the strictly
//   worse answer: it is the bug this file closes, and it makes the two columns that CAN be
//   answered unreadable too.
//
// ── PRECEDENCE AGAINST --test-data ───────────────────────────────────────────────────────
//   Unlike a virtual table, 2000000071 is a real SQL table and a restored backup can genuinely
//   carry rows for it. So the branch in GetDataAccessForTableCore lets the --test-data
//   on-demand loader run FIRST on a freshly created store, and this populator then does nothing
//   at all if the store already has any row. Real rows always win over synthesised ones; the
//   synthesis is the fallback for the (normal) case of a run with no application database.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine and Types-assembly members only (SystemTables, NavEnvironment, NCLMetaTable,
//   NCLMetaField, NavValue, ReadOnlyRecordBuffer, TempTableDataProvider), reached the same way
//   every sibling provider in this directory reaches them. No AL business-logic body is touched.

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    private static readonly ConditionalWeakTable<object, object> _omsPopulatedByProvider = new();

    private static int[]? _omsApplicationDatabaseTableIds;
    private static int? _omsObjectTypeOrdinal;

    /// <summary>True if <paramref name="table"/> is the Object Metadata system table (2000000071).</summary>
    private static bool IsObjectMetadataSystemTable(NCLMetaTable? table)
        => table != null && table.TableId == ObjectMetadataSystemTableId;

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
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — data access has no in-memory provider; see docs/scope.md");

        // One populate per provider; the row set never grows within a run.
        lock (_omsPopulatedByProvider)
        {
            if (_omsPopulatedByProvider.TryGetValue(provider, out _)) return;
            _omsPopulatedByProvider.Add(provider, provider);
        }

        // --test-data (or an install baseline) already put real rows here — leave them alone.
        if (ProviderHasAnyRow(provider)) return;

        var objectTypeOrdinal = EnsureObjectMetadataObjectTypeOrdinal(metaTable);
        var emitVersion = ReadNavEnvironmentEmitVersion();

        foreach (var tableId in EnumerateApplicationDatabaseTableIds())
        {
            InsertVirtualRow(provider, metaTable,
                new object[] { ObjectMetadataSystemTableId, objectTypeOrdinal, tableId, emitVersion },
                field => BuildObjectMetadataValue(field, objectTypeOrdinal, tableId, emitVersion));
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
    /// <c>Microsoft.Dynamics.Nav.Types.SystemTables.ApplicationDatabaseTables</c> — the very
    /// collection the cleanup migration interpolates into its DELETE, so the row set here and
    /// the row set a real tier retains cannot disagree. Ascending, so a FindLast without a
    /// filter and a FindLast filtered to Table agree about which row is last.
    /// </summary>
    private static int[] EnumerateApplicationDatabaseTableIds()
    {
        if (_omsApplicationDatabaseTableIds != null) return _omsApplicationDatabaseTableIds;

        var tSystemTables = ResolveType(
            "Microsoft.Dynamics.Nav.Runtime.SystemTables", "Microsoft.Dynamics.Nav.Types.SystemTables")
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — Microsoft.Dynamics.Nav.Types.SystemTables not found, so BC's "
                + "own application-database table list cannot be read; see docs/scope.md");

        var property = tSystemTables.GetProperty("ApplicationDatabaseTables",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — SystemTables.ApplicationDatabaseTables not found; see docs/scope.md");

        var ids = new List<int>();
        if (property.GetValue(null) is IEnumerable values)
            foreach (var v in values)
                if (v is int id) ids.Add(id);

        if (ids.Count == 0)
            throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — SystemTables.ApplicationDatabaseTables is empty, so there is "
                + "no row set to answer with; see docs/scope.md");

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
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — metatable has no field 3 (\"Object Type\"); see docs/scope.md");

        var optionString = field.FieldOptionMetadata?.OptionString
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — \"Object Type\" carries no option metadata; see docs/scope.md");

        var wanted = NormalizeObjectTypeName(ObjectMetadataRetainedObjectType);
        var parts = optionString.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (NormalizeObjectTypeName(parts[i]) != wanted) continue;
            _omsObjectTypeOrdinal = i;
            return i;
        }

        throw new RunnerOutOfScopeException(
            "Object Metadata (system table 2000000071)",
            $"object-metadata-system-table — \"Object Type\" option string ('{optionString}') has no "
            + $"'{ObjectMetadataRetainedObjectType}' member, so the ordinal every retained row carries "
            + "cannot be resolved; see docs/scope.md");
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
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — NavEnvironment not found, so BC's own emit version (the third "
                + "primary-key field) cannot be read; see docs/scope.md");

        var instance = tNavEnvironment
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null)
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — NavEnvironment.Instance is null, so BC's own emit version (the "
                + "third primary-key field) cannot be read; see docs/scope.md");

        var emitVersion = tNavEnvironment
            .GetProperty("EmitVersion", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(instance)
            ?? throw new RunnerOutOfScopeException(
                "Object Metadata (system table 2000000071)",
                "object-metadata-system-table — NavEnvironment.EmitVersion not found; see docs/scope.md");

        return (int)emitVersion;
    }

    /// <summary>
    /// True when the in-memory provider already holds at least one row. Reads the same
    /// <c>TempTableDataProvider.primaryTree</c> the stored-table census reads, where a null
    /// tree is BC's own representation of "no row was ever inserted".
    ///
    /// A layout change in BC's private fields answers FALSE (i.e. "populate"), not "leave it
    /// alone": the failure this guards against is shadowing real --test-data rows, and a run
    /// with no test data must still get its rows.
    /// </summary>
    private static bool ProviderHasAnyRow(object provider)
    {
        try
        {
            var field = provider.GetType().GetField("primaryTree",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field?.GetValue(provider) is not IEnumerable tree) return false;
            foreach (var _ in tree) return true;
            return false;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
    }
}
