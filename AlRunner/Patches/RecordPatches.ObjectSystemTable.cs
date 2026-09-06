// RecordPatches.ObjectSystemTable — what the runner does about the "Object" (2000000001)
// application-database system table.
//
// SHORT VERSION: it holds no rows, because a real service tier holds no rows. This file used
// to project the runner's own object inventory into it; a tier measured that as wrong and the
// projection is gone (issue #3071). What is left here is the one thing about this table that
// is NOT about rows — the four columns BC's own AL compiler reads as OemText.
//
// ── HOW THIS WAS SETTLED, AND BY WHAT ────────────────────────────────────────────────────
//   #2774 gave this table rows. The reasoning was that Object Metadata's own declared relation
//
//       field(6; "Object ID"; Integer)
//       {
//           TableRelation = Object.ID WHERE(Type = FIELD("Object Type"));
//       }
//
//   pointed at an empty table, and that an empty table was therefore a silent wrong answer —
//   silent because Microsoft has the accompanying TestTableRelation line commented out, so
//   nothing raises when the relation resolves to nothing.
//
//   THAT INFERENCE WAS WRONG, and it was wrong in the direction the schema invites: a declared
//   relation is not evidence that its target table is populated. On a real tier the relation
//   resolves to nothing, because the target table is empty.
//
//   The measurement. Corpus tests/al-language-onprem/record/TestObjectSystemTable.al
//   (codeunit 61202, corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#197, merged as
//   c04b236) asks a real service tier, from a Target = OnPrem app, what this table holds. It
//   ran on the BC OnPrem legs 27.0, 27.3, 28.0, 28.1, 28.2, 28.3 and 28.4 and passed on every
//   one of them. (The 28.4-generation eighth leg, 27.5, failed before executing anything: its
//   app publish came back HTTP 401/422 and all three OnPrem codeunits reported no result at
//   all — an infrastructure failure on that leg, not a verdict.)
//
//   Its centerpiece, Object_HoldsNoRows_WhileObjectMetadataDoes, carries a CONTROL ARM for the
//   obvious objection: it reads the populated sibling "Object Metadata" first, in the same
//   session from the same app, so "Object is empty" cannot be an unreadable table misreported
//   as an empty one. That control passed on the same seven legs.
//
//   So the answer is not "nothing appears to write 2000000001" read off Microsoft's code. It
//   is a service tier saying the table is present, readable and empty — which outranks reading
//   the AL, a container differential, the documentation, and the name of a codeunit
//   (.claude/rules/ask-the-corpus-before-claiming-bc-behavior.md).
//
// ── WHY THE TABLE IS EMPTY ON A REAL TIER ────────────────────────────────────────────────
//   2000000001 is NOT a virtual table. It has no DataProvider in Ncl.dll and is one of the 43
//   ids in Microsoft.Dynamics.Nav.Types.SystemTables.ApplicationDatabaseTables — a real SQL
//   table in the APPLICATION database, with a real schema. Its System.app declaration
//   (src/Application Database Tables/Object.Table.al) calls it the "legacy object metadata
//   storage system superseded by Application Object Metadata table" and declares it
//   ObsoleteState = Pending.
//
//   Having a schema is not the same as having rows. The classic object registry this table
//   served was written by a development environment that no longer exists; the modern publish
//   path writes "Object Metadata" (2000000071) and "Application Object Metadata" (2000000207).
//   The tier confirms the consequence: no row for any object, including objects published
//   moments before the read.
//
// ── WHAT THE RUNNER THEREFORE DOES ───────────────────────────────────────────────────────
//   Nothing. 2000000001 has no branch in GetDataAccessForTableCore any more; it falls through
//   to the generic path every other application-database table takes, which materialises an
//   empty store and gives the --test-data on-demand loader its chance to fill it. That is the
//   SAME call — GetOrCreateHydratedDataAccess — the deleted branch made, minus the populate,
//   so a --test-data backup's real rows still land exactly as they did.
//
//   `Record "Object"` therefore stays declarable, openable and readable: Count answers 0,
//   IsEmpty answers true, FindSet answers false, and Get raises BC's own "cannot find" error.
//   Empty is the answer, not a refusal — corpus 61202's Object_Get_UnknownKey_RaisesRecordNotFound
//   pins the Get half of that on a real tier, and the runner-extras suite pins the rest here.
//
//   THREE ISSUES CLOSE ON THE SAME REMOVAL, because all three were consequences of rows that
//   should not exist:
//     * #3071 — this one: reconcile the row set with the tier.
//     * #3096 — eleven registry columns (Modified, Compiled, "BLOB Reference", "BLOB Size",
//       "DBM Table No.", Date, Time, "Version List", Caption, Locked, "Locked By") answered
//       with BC's default instead of refusing by name, which .claude/rules/loud-failures.md
//       forbids. There is now no synthesised row to read them off. The only rows that can
//       exist are a --test-data backup's, and every column of those has a real source, so
//       there is nothing left to refuse — registering the table in
//       RecordPatches.NoSourceColumns.cs would make a backup's genuine data unreadable.
//     * #2839 — whether this table's field 20 holds the object's AL Caption. Moot for the same
//       reason: the question only existed because the projection had to decide what to put
//       there. A backup's Caption is whatever the backup holds.
//
// ── WHAT IS KEPT, AND WHY IT IS NOT ABOUT ROWS ───────────────────────────────────────────
//   IsOemTextFieldOnObjectTable, below. It is a fact about the table's FIELD METADATA, and it
//   is needed whether the table holds zero rows or a backup's thousand: without it every AL
//   read of Name / "Company Name" / "Version List" / "Locked By" throws
//   NavObjectDefinitionChangedException before it can return anything. Corpus 61202's own
//   Object_Get_UnknownKey_RaisesRecordNotFound reaches it — it compiles a Get against this
//   table — and that test passes here.
//
// PRECOMPILED-DLL RESPECT
//   Nothing in this file touches an AL business-logic body. What remains is one predicate over
//   a table id and a field number, mirroring a method on the shipped AL compiler.

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    internal const int ObjectSystemTableId = 2000000001;

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
    /// and "new" is the runner's metadata.</para>
    ///
    /// <para>KEPT AFTER THE ROW PROJECTION WAS REMOVED (#3071), deliberately. It is a claim
    /// about the table's field metadata, not about its rows: the exception above fires on the
    /// COMPILED READ, so it takes out a Get against an empty table exactly as it took out a
    /// read of a projected row. A --test-data backup's rows need it for the same reason.</para>
    /// </summary>
    internal static bool IsOemTextFieldOnObjectTable(int tableId, int fieldNo)
        => tableId == ObjectSystemTableId && fieldNo is 2 or 4 or 12 or 50;
}
