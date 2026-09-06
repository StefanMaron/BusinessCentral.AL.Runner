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
//   NOTE WHAT THAT DELETE DOES AND DOES NOT ESTABLISH. It bounds the retained set from ABOVE:
//   nothing outside "Object Type = Table over ApplicationDatabaseTables" survives. It does NOT
//   create a row per id, so on its own it proves a SUBSET relation, not equality. An earlier
//   version of this header rested the row set on it and claimed the runner and a real tier
//   "cannot disagree about which ids belong" — a stronger claim than that evidence carries.
//
//   THE INSERT SIDE IS WHAT SELECTS THE ROWS, in
//   Microsoft.BusinessCentral.InPlacePublisher.InPlacePublisher.UpsertIntoMetadataStorageImpl,
//   on the branch taken when the package being published is the System app
//   (record.RuntimePackageId == NavAppPackageCompiler.InternalSystemAppRuntimePackageId):
//
//        List<NavAppObjectMetadata> records = (from m in MovedObjectHelpers.ExcludeMovedObjects(outputter)
//            where m.ObjectType == ObjectType.Table
//            where SystemTables.ApplicationDatabaseTables.Contains(m.ObjectId)
//            where NCLMetaTable.GetStaticTableMetadataXml(m.ObjectId) == null
//            select m).ToList();
//        objectMetadataUpdater.UpdateOrInsertMetadataRecords(records, nodeId);
//
//   Three filters over the System app's own compilation output. Two are no-ops for this id
//   range, checked rather than assumed:
//     * GetStaticTableMetadataXml returns non-null for exactly ONE id — 2000001071, "Object
//       Metadata Snapshot" — and throws for seven withdrawn ids. For every id in
//       ApplicationDatabaseTables it returns null.
//     * ExcludeMovedObjects is a pass-through unless the package carries a MovedObjectManifest
//       resource. System.app carries none on BC 27.0 or BC 28.1.
//   So the row set is (System.app's table objects) INTERSECT ApplicationDatabaseTables.
//   Enumerating the .al sources shipped inside System.app: on BOTH BC 27.0 and BC 28.1 every
//   one of the 43 ids has a table object, none missing, highest 2000000400.
//
// ── SETTLED BY A SERVICE TIER, AND WHAT IT TOOK ──────────────────────────────────────────
//   This section used to open "NO SERVICE TIER HAS CONFIRMED THIS ROW SET". One has, since
//   StefanMaron/BusinessCentral.AL.Language.Tests#179 merged: the corpus grew a Target = OnPrem
//   app, and tests/al-language-onprem/record/TestObjectMetadataSystemTable.al measures the row
//   set on all eight OnPrem legs, BC 27.0 through 28.4. It confirms, against a real tier:
//
//     * 43 rows under Object Type = Table — one per id on
//       SystemTables.ApplicationDatabaseTables, exactly the equality the insert predicate and
//       Microsoft's DELETE each bound from one side without establishing;
//     * every row carries Object Type = Table;
//     * ObsoleteState = Pending does NOT keep an id off the list (2000000001);
//     * ObsoleteState = Removed does NOT either (2000000151) — which was the one sub-question
//       this comment recorded as open on Microsoft-code reading alone;
//     * virtual system tables (2000000026, 2000000038) and ordinary application tables (18)
//       get no row;
//     * FindLast under a Table filter lands on 2000000400;
//     * "Emit Version" is the build's own emit version, uniform within one published app.
//
//   WHY THAT TOOK A SECOND CORPUS APP. Corpus PR #153 tried it from the Cloud-target app and
//   was withdrawn: all 8 BC legs of run 33968379281 refused the only route such an app has —
//   "You cannot open record 2000000071 from a RecordRef data type when you are using target
//   Cloud" (NavRecordRef.CheckIsOpenAllowed; 2000000071 is in SystemTables.InternalTables, and
//   the escape hatch SystemTables.OnPremSystemTableRecordRefAllowed is only {2000000187,
//   2000000188}). Both refusals are decided by the CALLING APP'S compilation target and by
//   nothing else — NavRecordRef.IsOpenAllowed returns true outright for an OnPrem target — so
//   one app with a different target was all it took.
//
//   Everything in the section above is still read off Microsoft's code, which
//   .claude/rules/ask-the-corpus-before-claiming-bc-behavior.md ranks BELOW a tier verdict. It
//   is kept because it explains WHY the row set is what it is; the tier verdict is what makes
//   it true. Where they ever disagree, the tier wins and
//   EnumerateApplicationDatabaseTableIds is the one place to fix it.
//
//   A hypothesis raised in review and CHECKED, recorded so it is not re-raised: that 2000000004
//   (Permission Set) and 2000000005 (Permission) are excluded because SystemTables.VirtualTables
//   appends them when UsePermissionSetsFromExtensions is on (it defaults to true), leaving 41
//   rows. The insert predicate above never consults IsVirtualTable or VirtualTables — that
//   setting routes DATA ACCESS for those tables, it does not decide whether their compiled
//   metadata is published — and both are ordinary Scope = Cloud, ObsoleteState = Pending table
//   objects present in System.app on 27.0 and 28.1. So 43, not 41, on this evidence.
//
//   What is STILL not tier-adjudicated: the compiled-metadata payload columns below. The
//   upstream file asserts CalcFields(Metadata) has a payload on a real tier, and the runner
//   declares its blank answer as an expect-divergence rather than pretending otherwise.
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
// ── THE REFUSALS: WHAT EACH ONE CLAIMS, AND WHY NONE OF THEM IS A SCOPE BOUNDARY (#2894) ──
//   Every refusal in this file goes through ObjectMetadataShapeGap. All twelve used to end
//   "; see docs/scope.md", which was wrong twice over: docs/scope.md contains no
//   object-metadata text (the write-up is docs/limitations.md#object-metadata-system-table),
//   and citing scope.md ASSERTS that the surface is out of scope forever. It is not. Object
//   Metadata is an AL record on a real BC table and this file implements it;
//   .claude/rules/loud-failures.md puts AL records squarely in scope.
//
//   So none of the twelve is category (1) "genuinely out of scope", and none is category (3)
//   "implementable now" — there is nothing to build, they are preconditions that hold in every
//   supported configuration. All twelve are category (2): IN SCOPE, and the runner cannot
//   answer for the shape it actually found. Reason anchor "not-yet-implemented". What the
//   anchor tracks is issue #2946: the runner has no exception that says "I could not read BC's
//   internals here", and the three conventions in this directory disagree about which to use.
//
//     PopulateObjectMetadataSystemTable
//       1. data access has no in-memory provider
//          -> (2) the runner's own store wiring did not hand one over. Nothing to populate,
//             and inventing a store would answer with rows nobody can read back.
//     EnumerateApplicationDatabaseTableIds
//       2. SystemTables type not found          -> (2) unsupported BC assembly shape
//       3. ApplicationDatabaseTables not found  -> (2) unsupported BC assembly shape
//       4. ApplicationDatabaseTables is empty   -> (2) BC's own list is the row set; with no
//          list there is no row set, and a hardcoded fallback would be an invented answer.
//     EnsureObjectMetadataObjectTypeOrdinal
//       5. metatable has no field 3             -> (2) unsupported artifact metadata shape
//       6. "Object Type" has no option metadata -> (2) same
//       7. option string has no "Table" member  -> (2) same. Refusing beats guessing ordinal
//          1: the ordinal is a primary-key value, so a wrong guess silently mis-keys 43 rows.
//     ReadNavEnvironmentEmitVersion
//       8. NavEnvironment type not found        -> (2) unsupported BC assembly shape
//       9. NavEnvironment.Instance is null      -> (2) skeleton state not initialised at this
//          point. Note the difference from a skeleton Instance whose EmitVersion READS 0: that
//          0 is BC's own answer and is kept (see the columns section). A null Instance has no
//          answer at all, and this is the third primary-key field.
//      10. EmitVersion property not found       -> (2) unsupported BC assembly shape
//     ProviderHasAnyRow (both added by #2837)
//      11. primaryTree field not found          -> (4) BC's private provider layout moved
//      12. primaryTree is not enumerable        -> (4) same
//
//   BUCKET (4), ADDED BY #2946: the runner could not READ BC's internals. These two are the
//   only sites in this file in it, and they raise BcShapeGapException rather than
//   RunnerOutOfScopeException — see AlRunner/Infrastructure/BcShapeGapException.cs. The line
//   is whether the runner obtained the information at all. Sites 2, 3, 8 and 10 are the same
//   family (a BC type or member that is not there) and are NOT converted here, because
//   reclassifying them one at a time across this file and the 48 sites in
//   RecordPatches.VirtualTableShapeGap.cs is a per-site sweep with its own issue; #2946 is the
//   type convention, and this file's two primaryTree readers are the defect it names.
//   Sites 1, 4, 5, 6, 7 and 9 stay in bucket (2) on purpose and would be WRONG in (4): each of
//   those reads SUCCEEDED and the answer was merely unwelcome — BC's list came back empty, the
//   artifact genuinely has no field 3, a skeleton singleton the RUNNER populates is null, the
//   runner's own store wiring handed no provider over. An unwelcome answer is not an
//   unreadable one.
//
//   The same "; see docs/scope.md" wording sits on the equivalent provider-null guard in
//   roughly fifteen sibling virtual-table populators in this directory (AllObj, Integer,
//   Field, ...). Same defect, different files; issue #2945 tracks that sweep rather than
//   widening this change past the file the issue named.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine and Types-assembly members only (SystemTables, NavEnvironment, NCLMetaTable,
//   NCLMetaField, NavValue, ReadOnlyRecordBuffer, TempTableDataProvider), reached the same way
//   every sibling provider in this directory reaches them. No AL business-logic body is touched.

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
    /// The one place a refusal for this table is built — see the REFUSALS section of the file
    /// header for the per-site classification, and .claude/rules/loud-failures.md for the rule.
    ///
    /// <para>Reason anchor <c>not-yet-implemented</c>, deliberately. Object Metadata is IN
    /// SCOPE — this file implements it — so none of these twelve is a scope boundary; each one
    /// says the runner cannot answer for the shape it found. That is not cosmetic: for an AL
    /// <c>[TryFunction]</c>, ApplicationObjectBasePatches.IsPermanentOutOfScope traps a refusal
    /// into <c>false</c> UNLESS the reason starts <c>not-yet-implemented</c>. Under the old
    /// <c>object-metadata-system-table</c> anchor a runner gap here read as a clean
    /// <c>if not TryX()</c>, which is the silent default loud-failures.md forbids. Now it tears
    /// through.</para>
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
            if (ProviderHasAnyRow(provider)) return;

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
    /// <para>WHY THE FAILURE PATH EXISTS (#2786 review). The claim below is taken BEFORE any
    /// of the work, which is what makes the populate once-only under the concurrent handout
    /// in GetDataAccessForTableCore. Ten things after that claim can throw:
    /// <see cref="ProviderHasAnyRow"/>, the nine <see cref="RunnerOutOfScopeException"/>
    /// throws across <see cref="EnsureObjectMetadataObjectTypeOrdinal"/>,
    /// <see cref="ReadNavEnvironmentEmitVersion"/> and
    /// <see cref="EnumerateApplicationDatabaseTableIds"/>, and an <c>InsertVirtualRow</c> that
    /// fails part-way through the row set. Left alone, every one of them marked table
    /// 2000000071 "populated" holding whatever it had at the moment it failed — usually
    /// nothing — and every later access read an empty table with no diagnostic.</para>
    ///
    /// <para>THAT IS REACHABLE FROM AL, not just in theory. A runner refusal IS catchable:
    /// AL's <c>asserterror</c> is MethodScopePatches.NavMethodScope_AssertError, an unfiltered
    /// <c>catch (Exception ex)</c>, and this populate runs on the record-open path an
    /// <c>asserterror</c> can wrap. So the refusal gets swallowed and the empty table is what
    /// the rest of the test sees.</para>
    ///
    /// <para>A REFUSAL IS REPLAYED, NOT RETRIED. Retrying looks kinder and is worse: an
    /// <c>InsertVirtualRow</c> that failed on row 20 leaves 19 rows behind, so the retry's own
    /// <see cref="ProviderHasAnyRow"/> answers "already populated" and returns quietly — a
    /// silently PARTIAL table, which is the bug this method exists to prevent wearing a
    /// different hat. None of these failures is transient anyway: BC's layout does not change
    /// mid-run. Same principle as RowVersionPatches' "no loud-once-then-silent latch"
    /// (#1986), in the opposite direction — there the latch made a failure go quiet, here it
    /// is what keeps it loud.</para>
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
    /// <c>Microsoft.Dynamics.Nav.Types.SystemTables.ApplicationDatabaseTables</c> — the very
    /// collection Microsoft's publisher intersects with the System app's own table objects to
    /// decide which rows to INSERT, and the one its cleanup migration interpolates into its
    /// DELETE. Ascending, so a FindLast without a filter and a FindLast filtered to Table agree
    /// about which row is last.
    ///
    /// <para>This is an UPPER BOUND on what a real tier holds, not a confirmed equality. No
    /// service tier has adjudicated it — see the file header for why the upstream corpus test
    /// could not be written, and for the 11 <c>ObsoleteState = Removed</c> ids that are the
    /// open part of the question. If a tier ever disagrees, this method is the one place to
    /// filter.</para>
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
    /// <para>THE TWO REASONS THIS READ CAN COME BACK EMPTY ARE NOT THE SAME REASON (#2786).
    /// A null <c>primaryTree</c> is BC's own "no row was ever inserted", and answering FALSE —
    /// "go ahead and synthesise" — is right. The field being ABSENT means BC renamed or
    /// restructured it and the runner has no idea what the store holds; answering FALSE there
    /// would silently shadow whatever --test-data restored into this table, disabling the
    /// precedence rule in this file's header with no diagnostic anywhere. It used to do
    /// exactly that. Now it refuses, naming the member. See .claude/rules/loud-failures.md.</para>
    ///
    /// <para>THE TYPE IS <see cref="BcShapeGapException"/>, NOT
    /// <see cref="RunnerOutOfScopeException"/> (#2946). Both of these two refusals are BUG
    /// REPORTS ABOUT THE RUNNER rather than scope boundaries, and this file used to say so in a
    /// comment and then raise the scope exception anyway — which is the disagreement #2946 was
    /// filed about: four readers of this same private structure raised three different types
    /// between them. Two consequences follow the type rather than the wording. A shape gap can
    /// never be absorbed by an <c>expect-oos</c> manifest entry, so a BC-layout regression
    /// cannot be declared expected — it is a property of which BC build is on disk, not of the
    /// runner, so it can be true on one BC leg and false on another in the same run. And it
    /// tears through AL's <c>asserterror</c> as well as through a <c>[TryFunction]</c>, which a
    /// <see cref="RunnerOutOfScopeException"/> does not: <c>asserterror</c> is
    /// MethodScopePatches.NavMethodScope_AssertError, and on real BC this record-open SUCCEEDS,
    /// so catching the gap would make an <c>asserterror</c> around it pass where BC fails it.
    /// <see cref="RunObjectMetadataPopulateOnce"/> still remembers a refusal, because the other
    /// ten throw sites in this file remain catchable.</para>
    ///
    /// <para>Resolution goes through <see cref="BcShape"/>, which uses
    /// <see cref="PrivateMemberLookup"/> rather than a plain <c>GetField</c>, because
    /// <c>primaryTree</c> is PRIVATE on <c>TempTableDataProvider</c> and
    /// <c>GetField(NonPublic)</c> on a DERIVED type does not return a base class's private
    /// fields — BC's own <c>CrmTableConnection.CrmTestDataProvider</c> derives from it (#2725).
    /// Reading a derived provider's inherited field as "absent" would turn a perfectly readable
    /// store into a hard failure now that absence refuses.</para>
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
