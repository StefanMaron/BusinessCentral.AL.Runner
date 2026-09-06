// RecordPatches.ObjectSystemTable — managed row source for the "Object" (2000000001)
// application-database system table.
//
// WHY THIS EXISTS (issue #2774)
//   Object routed to the same empty in-memory store as every other table the runner has no
//   rows for, so `Record "Object"` answered nothing for every object — including objects the
//   runner had compiled moments earlier. It is the other half of Object Metadata's own
//   declared table relation:
//
//       field(6; "Object ID"; Integer)
//       {
//           TableRelation = Object.ID WHERE(Type = FIELD("Object Type"));
//       }
//
//   #2519 filled 2000000071 and left 2000000001 empty, so the relation pointed at nothing.
//   Microsoft has the `TestTableRelation` line on that field commented out ("This property is
//   currently not supported"), so nothing raised: the empty table was a silent wrong answer.
//
// ── WHAT REAL BC HAS IN THIS TABLE ───────────────────────────────────────────────────────
//   2000000001 is NOT a virtual table. It has no DataProvider in Ncl.dll and is one of the 43
//   ids in Microsoft.Dynamics.Nav.Types.SystemTables.ApplicationDatabaseTables — a real SQL
//   table in the APPLICATION database. Its System.app declaration
//   (src/Application Database Tables/Object.Table.al) calls it the "legacy object metadata
//   storage system superseded by Application Object Metadata table", declares it
//   Scope = OnPrem and ObsoleteState = Pending, and keys it on Type + "Company Name" + ID.
//
//   Unlike Object Metadata, whose row set is a fixed BC-declared list of table ids, Object's
//   rows are an OBJECT INVENTORY: one row per application object. The runner already has
//   exactly one such inventory — EnumerateKnownAlObjects, the source AllObj (2000000038) and
//   AllObjWithCaption (2000000058) are answered from. This file projects that same inventory
//   into Object's column shape rather than building a second one.
//
//   That does NOT make the two tables agree about which objects exist, and saying so would
//   overstate it: Object's "Type" option cannot name an enum, an interface, a permission set
//   or any *extension kind, so it lists strictly fewer KINDS than AllObj by design (see
//   below). What the shared inventory buys is narrower and still worth having — for the kinds
//   BOTH tables can name, neither can list an object the other does not, or give it a
//   different id or name, because there is only one place the answer comes from.
//
// ── WHAT IS NOT SETTLED — AND THE BLOCKER THAT USED TO MAKE IT UNSETTLEABLE IS GONE ──────
//   NO SERVICE TIER HAS CONFIRMED WHAT THIS TABLE HOLDS ON A REAL TIER. That much is still
//   true. What is NO LONGER true is the reason this comment used to give for it — that the
//   claim "cannot be expressed" in the al-language corpus. Tracked as AlRunner#3071.
//
//   The two blockers, and why neither applies any more:
//     * "the corpus app targets Cloud and this table is Scope = OnPrem, so `Record "Object"`
//       fails AL0296 at compile there". The corpus gained a SECOND app in corpus PR #179 —
//       tests/al-language-onprem, Target = OnPrem, id range 61200-61299 — for exactly this
//       class of table. AL0296 does not fire there.
//     * "the RecordRef route is refused at RUNTIME". It is, for a Cloud target, and that is
//       MEASURED: corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#153 tried it on the
//       sibling id 2000000071 and was withdrawn after all 8 BC legs of run 33968379281 refused
//       it before a single assertion ran (2000000001 follows by membership in the same
//       SystemTables.InternalTables FrozenSet, not by its own measurement). But
//       NavRecordRef.IsOpenAllowed reads, in full:
//
//           private bool IsOpenAllowed(CompilationTarget compilationTarget, int tableId)
//           {
//               if (!compilationTarget.IsOnPremTarget())
//                   return IsSystemTableAllowedForRecordRefUsage(tableId);
//               return true;
//           }
//
//       so the InternalTables test is never reached for an OnPrem target — and an OnPrem app
//       needs no RecordRef here anyway, it can declare `Record Object` directly.
//
//   Corpus #179 and #187 have since put two other Scope = OnPrem system tables in front of a
//   real tier from that app, green on all eight OnPrem legs, and #187 contradicted two
//   runner-local assertions in the process (AlRunner#3066). This one has not been asked yet.
//   Leaving the old wording in place is how those two survived, so it is corrected rather than
//   kept: the work is available, not impossible.
//
//   So what this file answers is deliberately the part that needs no tier verdict: the rows
//   are the runner's OWN object inventory, and the claim "Object lists the objects this run
//   knows about" is a claim about the runner. Nothing here asserts that a real tier's Object
//   table holds the same set.
//
// ── THE COLUMNS, AND WHICH ONES ARE A DECLARED DIVERGENCE ────────────────────────────────
//   Answered from the shared inventory:
//     Type           — option ordinal resolved BY NAME out of the metatable's own option
//                      string (never a hardcoded ordinal). An AL object kind the option
//                      string cannot name — Enum, Interface, PermissionSet, any *extension —
//                      gets NO ROW, because inventing an ordinal for it would put the object
//                      under a type it is not. Object's option is
//                      TableData,Table,,Report,,Codeunit,XMLport,MenuSuite,Page,Query,System,
//                      FieldNumber, so it is a strict subset of AllObj's.
//     "Company Name" — blank. Every object the runner knows is company-independent; the
//                      column exists for the classic per-company object registry, which the
//                      runner has no equivalent of. Written explicitly rather than left to
//                      the generic default because it is the second primary-key field.
//     ID             — the object id.
//     Name           — the object name.
//
//   Every one of those goes through BC's own NavValue.CreateNavValueFromObject, which builds
//   the value at whatever type the field's metadata declares. That matters here more than it
//   does for AllObj: Object's Name, "Company Name", "Version List" and "Locked By" are
//   OemText, not Text, and BC's own AL COMPILER is where that is decided — see
//   IsOemTextFieldOnObjectTable below. Before that was handled, `Obj.Name` did not return a
//   wrong value, it threw NavObjectDefinitionChangedException, so the rows were unreadable
//   even once they existed.
//
//   NOT answered — everything the classic object registry stored and the runner has no
//   source for:
//     Modified, Compiled, "BLOB Reference", "BLOB Size", "DBM Table No.", Date, Time,
//     "Version List", Caption, Locked, "Locked By".
//
//   Those get BC's own NavValue.GetDefaultNavValue — false, 0, an empty BLOB, the empty
//   string, 0D, 0T (and, for "Version List" / "Locked By", an empty OemText). THIS IS A DECLARED DIVERGENCE, NOT A FAITHFUL SUBSTITUTION: it is
//   recorded in docs/limitations.md and asserted by tests/runner-extras/object-system-table so
//   it cannot change quietly. Per .claude/rules/loud-failures.md those columns should refuse
//   BY NAME rather than read blank; that needs the same per-(table, field) read seam on the
//   shared TempTableDataProvider path that #2771 tracks for Object Metadata's payload columns,
//   and this table is a second consumer of it. Throwing at row-build time instead is not an
//   option — it would take out FindSet / Count / Get as well, which is the bug this file
//   closes.
//
//   Caption is deliberately in the blank list even though the shared inventory DOES carry a
//   caption for most objects (AllObjWithCaption is answered from it). Whether this legacy
//   table's field 20 holds the object's AL Caption is a claim about BC that no tier can
//   adjudicate here, and #2774 asks only for Type / ID / Name. Issue #2839 tracks it rather
//   than guessing.
//
// ── PRECEDENCE AGAINST --test-data ───────────────────────────────────────────────────────
//   Like Object Metadata and unlike every virtual table, 2000000001 is a real SQL table and a
//   restored backup can genuinely carry rows for it. So the branch in GetDataAccessForTableCore
//   lets the --test-data on-demand loader run FIRST on a freshly created store, and the
//   populator below does nothing at all when that load actually produced rows for this table.
//   Real rows always win; the projection is the fallback for the (normal) case of a run with no
//   application database.
//
//   "Actually produced rows" is a fact the loader RECORDS (RecordPatches.BackupRowProvenance.cs,
//   #2875), not something read back off the store. Reading the store was the bug: an
//   install-baseline restore replays this projection's own rows into a brand-new provider, and
//   a provider cannot say who filled it, so the projection latched itself off for a store
//   holding nothing but its own stale output. The companion half is in
//   CaptureInstallBaselineSnapshot, which no longer captures this table while the projection
//   owns it — so there is nothing to replay in the first place, and the two halves together
//   close the ambiguity rather than narrow it.
//
// PRECOMPILED-DLL RESPECT
//   Runtime-engine and Types-assembly members only (NCLMetaTable, NCLMetaField, NavValue,
//   NavText, NavInteger, NavOption, ReadOnlyRecordBuffer, TempTableDataProvider), reached the
//   same way every sibling provider in this directory reaches them. No AL business-logic body
//   is touched.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int ObjectSystemTableId = 2000000001;

    private const int ObjectFieldType = 1;
    private const int ObjectFieldCompanyName = 2;
    private const int ObjectFieldId = 3;
    private const int ObjectFieldName = 4;

    /// <summary>
    /// Per-in-memory-provider state for the Object (2000000001) projection.
    /// <para><see cref="Inserted"/> makes the top-up idempotent per (type ordinal, id) so
    /// later handouts pick up objects registered since without re-inserting — and, just as
    /// importantly, without resurrecting a row AL has deleted in between.</para>
    /// <para>It is the only per-provider state left. "Does a --test-data backup own this
    /// table's rows" used to be latched here too, decided from the provider on first handout;
    /// #2875 moved it to <see cref="BackupOwnsRowsFor"/>, because a provider cannot say who
    /// filled it and a boundary restore hands out a new one carrying replayed rows.</para>
    /// </summary>
    private sealed class ObjectSystemTableState
    {
        internal readonly ConcurrentDictionary<(int TypeOrdinal, int Id), byte> Inserted = new();
    }

    private static readonly ConditionalWeakTable<object, ObjectSystemTableState> _objsStateByProvider = new();

    // Resolved once per process from the parsed Object metatable's own field-1 option string.
    private static Dictionary<string, int>? _objsTypeOrdinals;

    // NavValue.CreateNavValueFromObject(INavValueMetadata, object) — BC's own type-driven
    // conversion. Bound lazily; see BuildObjectValue for why it is used instead of naming a
    // concrete NavText/NavInteger factory.
    private static MethodInfo? _objsCreateNavValueFromObject;

    /// <summary>
    /// The four Object (2000000001) fields BC's own AL compiler reads as <c>OemText</c> rather
    /// than the <c>Text[n]</c> the table declares: 2 "Company Name", 4 "Name", 12 "Version
    /// List", 50 "Locked By".
    ///
    /// <para>This is not a guess and not a runner convention. It mirrors
    /// <c>Microsoft.Dynamics.Nav.CodeAnalysis.Emit.CodeGenerator.IsOemTextFieldOnObjectTable</c>,
    /// whose whole body is a table-id check against 2000000001 and a switch over exactly these
    /// four field numbers; <c>GetFieldType</c> calls it and substitutes
    /// <c>NavTypeKind.OemText</c>, which is what ends up in the
    /// <c>ValidateExpectedType(fieldNo, NavType.OemText)</c> call the emitted IL makes. Read
    /// off the shipped compiler assembly on BOTH BC 27.5.46862.48827 and BC 28.1.49838.53910 —
    /// byte-identical bodies, so it is stable across the supported range rather than a quirk of
    /// one build.</para>
    ///
    /// <para>Without this, the metatable takes <c>SymbolReference.json</c>'s declared
    /// <c>Text[30]</c> at face value and every AL read of those four columns throws
    /// <c>NavObjectDefinitionChangedException</c> — "The definition of the Name field has
    /// changed; old type: OemText, new type: Text" — where "old" is what the compiler baked in
    /// and "new" is the runner's metadata. Filling the table's rows without this fix would have
    /// left Name unreadable, which is most of the point of the table.</para>
    /// </summary>
    internal static bool IsOemTextFieldOnObjectTable(int tableId, int fieldNo)
        => tableId == ObjectSystemTableId && fieldNo is 2 or 4 or 12 or 50;

    /// <summary>True if <paramref name="table"/> is the Object system table (2000000001).</summary>
    private static bool IsObjectSystemTable(NCLMetaTable? table)
        => table != null && table.TableId == ObjectSystemTableId;

    /// <summary>
    /// Populate the in-memory store behind Object (2000000001) with one row per object the
    /// runner knows about, projected from the SAME inventory AllObj is answered from.
    ///
    /// No-op for the lifetime of a provider whose store already held a row on first touch —
    /// see the --test-data note in this file's header.
    /// </summary>
    private static void PopulateObjectSystemTable(object dataAccess, NCLMetaTable metaTable)
    {
        EnsureAllObjReflection(metaTable);
        EnsureDataAccessProviderReflection(dataAccess);

        var provider = _pDataAccessDataProvider!.GetValue(dataAccess)
            ?? throw new RunnerOutOfScopeException(
                "Object (system table 2000000001)",
                "object-system-table — data access has no in-memory provider; see docs/scope.md");

        // THE LATCH ASKS "DID SOMETHING OTHER THAN THIS PROJECTION PUT ROWS HERE?", and no
        // property of the PROVIDER can answer that. The old test — ProviderHasAnyRow, narrowed
        // by #2842 to runs that have a --test-data loader at all — read a store, and a store
        // does not remember who filled it. An install-baseline restore replays rows THIS
        // projection wrote earlier in the run into a BRAND-NEW provider, which gets a fresh
        // ConditionalWeakTable entry, so the projection read its own stale output as somebody
        // else's, latched, and never topped up that provider again. Harmless for Object
        // Metadata, whose row set is a fixed BC-declared id list identical whoever produced it;
        // NOT harmless here, where the row set is THIS run's object inventory.
        //
        // So the question is asked of the WRITER instead (#2875). The --test-data on-demand
        // load is the only other writer of this table, and it records the tables it actually
        // loaded rows into — see RecordPatches.BackupRowProvenance.cs. That fact outlives any
        // provider, so a restore cannot launder a projection into a backup.
        //
        // The other half of the same fix removes the ambiguity at its source: a projection-owned
        // Object table is no longer captured into an install baseline at all (the #2272
        // treatment, applied conditionally), so there is nothing for a restore to replay. The
        // two halves are deliberately both here — the exclusion means a restored provider can
        // only ever hold a backup's rows, and this check means the projection knows it.
        //
        // Deliberately NOT latched per provider any more. A latch was only ever a way to
        // remember the answer to a question that had to be asked at exactly the right moment;
        // provenance is a run-scoped fact, so it can be read on every touch and is the same
        // answer whichever provider is in hand. `Inserted` stays per provider, because THAT is
        // genuinely per-store state: it is what keeps the top-up idempotent without
        // resurrecting a row AL deleted in between.
        var state = _objsStateByProvider.GetValue(provider, static _ => new ObjectSystemTableState());

        // A --test-data backup's real rows are the better answer — leave them alone.
        if (BackupOwnsRowsFor(ObjectSystemTableId)) return;

        var ordinals = EnsureObjectTypeOrdinals(metaTable);

        foreach (var (kind, id, name, _) in EnumerateKnownAlObjects())
        {
            // The shared inventory carries a caption for AllObjWithCaption; Object's caption
            // column is a declared blank (see the header), so it is discarded here.
            if (id <= 0) continue;
            if (!ordinals.TryGetValue(NormalizeObjectTypeName(kind), out var typeOrdinal))
                // This AL object kind has no member in THIS artifact's Object "Type" option
                // string (Enum, Interface, PermissionSet, every *extension kind, …). The
                // legacy registry has no way to represent it, so it gets no row — inventing
                // an ordinal would file the object under a type it is not.
                continue;
            if (!state.Inserted.TryAdd((typeOrdinal, id), 0)) continue;

            var objectName = name ?? string.Empty;
            InsertVirtualRow(provider, metaTable,
                new object[] { ObjectSystemTableId, typeOrdinal, id, 0 },
                field => BuildObjectValue(field, typeOrdinal, id, objectName));
        }
    }

    /// <summary>
    /// One column of an Object row, matched by the metatable's own FIELD NUMBER — this table's
    /// columns are sparsely numbered (1-12, 20, 40, 50) and the four the runner answers are
    /// 1-4, so the number is the stable handle. Every other column is what the classic object
    /// registry stored and the runner has no source for; see the declared-divergence section
    /// of this file's header.
    /// </summary>
    private static object? BuildObjectValue(NCLMetaField field, int typeOrdinal, int objectId, string objectName)
        => field.FieldNo switch
        {
            ObjectFieldType => CreateNavValueForField(field, typeOrdinal),
            // Second primary-key field: written explicitly so the blank company is a decision,
            // not a side effect of the generic default below.
            ObjectFieldCompanyName => CreateNavValueForField(field, string.Empty),
            ObjectFieldId => CreateNavValueForField(field, objectId),
            ObjectFieldName => CreateNavValueForField(field, TruncateToFieldLength(field, objectName)),
            _ => _aovGetDefaultNavValue!.Invoke(null, new object?[] { field, false }),
        };

    /// <summary>
    /// Build the NavValue for <paramref name="field"/> through BC's own
    /// <c>NavValue.CreateNavValueFromObject(INavValueMetadata, object)</c>, which switches on
    /// the field's declared <c>NclType</c> and calls that type's own <c>CreateFromObject</c>.
    ///
    /// <para>Naming a concrete factory instead — <c>NavText.CreateTruncated</c>, the way the
    /// AllObj projection does — is what broke first here: Object's Name column is
    /// <c>OemText</c>, not <c>Text</c> (see <see cref="IsOemTextFieldOnObjectTable"/>), so a
    /// NavText landed in an OemText slot. Going through BC's dispatcher means the value's type
    /// is whatever the metadata says, for every column, without this file having to know which
    /// columns are which.</para>
    /// </summary>
    private static object CreateNavValueForField(NCLMetaField field, object value)
    {
        if (_objsCreateNavValueFromObject == null)
        {
            var tNavValue = ResolveType(
                "Microsoft.Dynamics.Nav.Runtime.NavValue", "Microsoft.Dynamics.Nav.Types.NavValue")
                ?? throw new RunnerOutOfScopeException(
                    "Object (system table 2000000001)",
                    "object-system-table — NavValue not found, so no column value can be built; "
                    + "see docs/scope.md");
            _objsCreateNavValueFromObject = tNavValue.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "CreateNavValueFromObject"
                    && m.GetParameters().Length == 2
                    && m.GetParameters()[1].ParameterType == typeof(object))
                ?? throw new RunnerOutOfScopeException(
                    "Object (system table 2000000001)",
                    "object-system-table — NavValue.CreateNavValueFromObject(INavValueMetadata, object) "
                    + "not found, so a column value cannot be built at the type BC's own metadata "
                    + "declares; see docs/scope.md");
        }

        return _objsCreateNavValueFromObject.Invoke(null, new object?[] { field, value })
            ?? throw new RunnerOutOfScopeException(
                "Object (system table 2000000001)",
                $"object-system-table — BC's own NavValue.CreateNavValueFromObject returned null for "
                + $"field {field.FieldNo} ('{field.FieldName}'); see docs/scope.md");
    }

    /// <summary>
    /// Cut <paramref name="text"/> to the field's own declared length before handing it to BC.
    /// AL caps object names at 30 characters and this column is declared 30, so in practice
    /// nothing is ever cut — the guard exists so a longer name from some future inventory
    /// source cannot turn into an exception from a NavText/NavOemText constructor.
    /// </summary>
    private static string TruncateToFieldLength(NCLMetaField field, string text)
    {
        var max = field.FieldDefinedLength;
        return max > 0 && text.Length > max ? text[..max] : text;
    }

    /// <summary>
    /// The Object "Type" option ordinals, read out of the parsed metatable's own field-1
    /// NCLOptionMetadata.OptionString and keyed by normalized option name. This is the
    /// authority for the mapping — never a hardcoded table, because the option string carries
    /// reserved blank slots (two of them on BC 27/28) and has gained members across versions.
    /// </summary>
    private static Dictionary<string, int> EnsureObjectTypeOrdinals(NCLMetaTable metaTable)
    {
        if (_objsTypeOrdinals != null) return _objsTypeOrdinals;

        var typeField = (GetAllFields(metaTable) ?? Enumerable.Empty<NCLMetaField>())
            .FirstOrDefault(f => f.FieldNo == ObjectFieldType)
            ?? throw new RunnerOutOfScopeException(
                "Object (system table 2000000001)",
                "object-system-table — metatable has no field 1 (\"Type\"), so no row's type can be "
                + "resolved; see docs/scope.md");

        var optionString = typeField.FieldOptionMetadata?.OptionString
            ?? throw new RunnerOutOfScopeException(
                "Object (system table 2000000001)",
                "object-system-table — \"Type\" carries no option metadata, so its ordinals cannot be "
                + "resolved; see docs/scope.md");

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var parts = optionString.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            var key = NormalizeObjectTypeName(parts[i]);
            if (key.Length == 0) continue;   // reserved blank slots are real members of the string
            map.TryAdd(key, i);
        }

        if (map.Count == 0)
            throw new RunnerOutOfScopeException(
                "Object (system table 2000000001)",
                $"object-system-table — \"Type\" option string is empty ('{optionString}'), so there is no "
                + "type to file any object under; see docs/scope.md");

        _objsTypeOrdinals = map;
        return map;
    }
}
