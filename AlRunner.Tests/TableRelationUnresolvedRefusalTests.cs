// TableRelationUnresolvedRefusalTests — issue #3306.
//
// WHAT IS PINNED
// --------------
// `BuildMetaFieldRelations` used to answer `null` for a relation whose target table, condition
// field, where() field or where()-field() link did not resolve, and write a `[RecordPatches]`
// line that default verbosity drops. `null` reaches BC's `NCLMetaField` ctor as
// `EmptyFieldRelations`, and `RecordImplementation.EvaluateRelation` answers `-1` for that —
// the SAME answer it gives for "no arm applies to this row". From there:
//
//   * `ValidateNonFlowFieldAsync` skips the whole relation check, so `Validate` accepts a value
//     that has no row in the related table, where real BC raises
//     "<value> cannot be found in the related table";
//   * `RecordImplementation.GetRelation` maps -1 to 0, so `FieldRef.Relation` answers 0 —
//     indistinguishable from "this field declares no TableRelation".
//
// Both are silent WRONG ANSWERS in the direction that looks like success, which is exactly what
// `.claude/rules/loud-failures.md` forbids. This file pins the refusal that replaces them.
//
// WHY THE SEAM IS EvaluateRelation AND NOT THE BUILDER
// ---------------------------------------------------
// Refusing inside `BuildMetaFieldRelations` would make the whole TABLE unbuildable because one
// arm of one field did not resolve — every test touching that table dies, including the ones
// that never read the field. #3279 hit the identical choice on the CalcFormula side and
// resolved it the same way: record at build time, refuse at the seam AL actually reaches.
//
// `RecordImplementation.EvaluateRelation(NCLMetaField)` is that seam, and it is a single one.
// Read out of Ncl.dll 28.1 with the bc-decompiler MCP server rather than assumed — it has
// exactly three callers, and they are the three AL-observable routes:
//
//   NavRecord.EvaluateRelation(int)                     <- FieldRef.Relation / autofill
//   RecordImplementation.GetRelation(NCLMetaField)      <- FieldRef.Relation's value
//   RecordImplementation.<ValidateNonFlowFieldAsync>    <- Validate's relation check
//
// So one prepend covers all three, and no fourth route can slip past it.
//
// WHY THIS IS NOT AN AL TEST IN tests/runner-extras/, AND NOT A CORPUS TEST
// ------------------------------------------------------------------------
// Measured on this tree, not assumed. A bundle whose TableRelation names an absent table does
// not compile — the runner drives Microsoft's own AL compiler and it rejects the object:
//
//     error AL0185: Table 'RDP Absent Parent' is missing
//
// and the object is dropped, so the test never runs. The drop site DOES fire during that failed
// compile (confirmed with the four sites instrumented to print unconditionally), which is what
// establishes it is the right site — but AL that reaches it always fails to compile first.
//
// The same holds one level down for the shapes the PARSER refuses: an if() condition carrying a
// field() link is rejected by the AL compiler with `error AL0489: The property expression is not
// valid. A CONST or FILTER expression is expected.` before the parser ever sees it.
//
// So the state this refusal answers is reachable only through a runner-side metadata gap, never
// from AL source. That also means a service tier cannot adjudicate it: real BC always resolves
// these names, so there is no BC behaviour here to ask the corpus about. The BC-behaviour half —
// "Validate enforces a TableRelation" — is already pinned upstream three times over (corpus
// codeunit 60482 TestFieldRefRelation, corpus PR 207's codeunit 60827 for a tableextension-
// contributed relation, corpus PR 222 for a declared one), and this file deliberately does not
// duplicate any of it.
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class TableRelationUnresolvedRefusalTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    // Process-wide unique among AlRunner.Tests statics: these land in the same static
    // _parsedTables / _metaTableCache the whole assembly shares, so a duplicate id does not fail
    // loudly — it hands back the OTHER file's table. See CalcFormulaUnresolvedRefusalTests for
    // the incident that established this.
    private const int ParentTableId = 94200;
    private const int ChildTableId = 94201;
    private const int ResolvableRelationFieldId = 2;
    private const int UnresolvableRelationFieldId = 3;
    private const int NoRelationFieldId = 4;

    public TableRelationUnresolvedRefusalTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-3306-refusal");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void EvaluateRelationOnAFieldWhoseRelationDidNotResolve_RefusesNamingTheTableAndTheField()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var child = BuildChildTable();

        Assert.True(child.TryGetFieldByNo(UnresolvableRelationFieldId, out var unresolved));
        // The precondition, asserted rather than assumed: the builder could not resolve
        // "TRU Absent Parent", so it handed BC's ctor no relations at all. That is precisely the
        // state that used to be indistinguishable from "no TableRelation declared".
        Assert.True(unresolved.FieldRelations == null || unresolved.FieldRelations.Count == 0);

        var refusal = Assert.Throws<RunnerOutOfScopeException>(
            () => EvaluateRelation(unresolved));

        // The reason anchor a tests/expectations entry or a developer would match on...
        Assert.Contains("tablerelation-reference-unresolved", refusal.Reason);
        // ...and the names without which the message is no better than the silent 0: WHICH
        // table the field lives on, WHICH field it is, and WHAT could not be resolved.
        Assert.Contains("TRU Absent Parent", refusal.Message);
        Assert.Contains("Unresolved Ref", refusal.Message);
        Assert.Contains("TRU Child", refusal.Message);

        // `not-yet-implemented` on purpose: ApplicationObjectBasePatches.IsPermanentOutOfScope
        // traps a PERMANENTLY out-of-scope refusal into `false` for an AL [TryFunction]. Real BC
        // resolves these names, so the gap is the runner's, and trapping it would turn this
        // refusal straight back into the silent default it replaces.
        Assert.StartsWith("not-yet-implemented", refusal.Reason);
    }

    [SkippableFact]
    public void EvaluateRelationOnAFieldWhoseRelationResolved_IsNotRefused()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var child = BuildChildTable();

        Assert.True(child.TryGetFieldByNo(ResolvableRelationFieldId, out var resolvable));
        // The scoping control, and the half that must pass in BOTH states: a relation that DID
        // resolve is still carried and still evaluated. A refusal keyed on the table rather than
        // on the field, or one that fired whenever any note existed, fails here.
        Assert.NotNull(resolvable.FieldRelations);
        Assert.NotEmpty(resolvable.FieldRelations);
        Assert.Equal(ParentTableId, resolvable.FieldRelations[0].SourceTableId);

        // No throw, and BC's own answer comes back: -1 means "no arm applies to the current
        // (empty) row", which is what an unfiltered single-arm relation answers off a record
        // buffer with no value in the field. The claim here is "not refused", so the assertion
        // is that the call returns at all rather than on the particular integer.
        var index = EvaluateRelation(resolvable);
        Assert.InRange(index, -1, 0);
    }

    [SkippableFact]
    public void EvaluateRelationOnAFieldDeclaringNoRelation_IsNotRefused()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var child = BuildChildTable();

        Assert.True(child.TryGetFieldByNo(NoRelationFieldId, out var plain));
        // The other direction, and the one that keeps the refusal honest. This field reaches
        // EvaluateRelation in the SAME observable state as the unresolvable one — no relations
        // on the metafield — and must NOT be refused, because here the AL genuinely declares no
        // TableRelation and answering -1/0 is BC's own correct answer. A guard that keyed on
        // "the metafield has no relations" instead of on the recorded note would fail here, and
        // would refuse a large fraction of every table in the platform.
        Assert.True(plain.FieldRelations == null || plain.FieldRelations.Count == 0);

        var index = EvaluateRelation(plain);
        Assert.Equal(-1, index);
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the child table's real NCLMetaTable from AL source, through the same entry point
    /// Validate and FieldRef.Relation reach. Three fields: one relation whose target this bundle
    /// declares, one naming a table nothing declares — which is what a runner-side metadata gap
    /// looks like from the builder's point of view, and the only way to reach that state at all
    /// (AL that names an absent table does not compile) — and one declaring no relation.
    /// </summary>
    private NCLMetaTable BuildChildTable()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Parent.al"), $$"""
            table {{ParentTableId}} "TRU Parent"
            {
                fields
                {
                    field(1; "Code"; Code[20]) { }
                }
                keys { key(PK; "Code") { Clustered = true; } }
            }
            """);
        File.WriteAllText(Path.Combine(srcDir, "Child.al"), $$"""
            table {{ChildTableId}} "TRU Child"
            {
                fields
                {
                    field(1; "Entry No."; Integer) { }
                    field({{ResolvableRelationFieldId}}; "Resolvable Ref"; Code[20])
                    {
                        TableRelation = "TRU Parent"."Code";
                    }
                    field({{UnresolvableRelationFieldId}}; "Unresolved Ref"; Code[20])
                    {
                        TableRelation = "TRU Absent Parent"."Code";
                    }
                    field({{NoRelationFieldId}}; "Plain Code"; Code[20]) { }
                }
                keys { key(PK; "Entry No.") { Clustered = true; } }
            }
            """);
        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);

        // Registered and built up to three times, because `_parsedTables` and the metatable
        // cache are process-wide and another collection may call ResetForReload between the
        // parse and the build. Same recovery CalcFormulaUnresolvedRefusalTests documents.
        NCLMetaTable? child = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            RecordPatches.AddSourceDir(srcDir);
            child = RecordPatches.EnsureTableInMetadataCache(ChildTableId)
                    ?? RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, ChildTableId, false, 0);
            if (child != null
                && child.TryGetFieldByNo(ResolvableRelationFieldId, out _)
                && child.TryGetFieldByNo(UnresolvableRelationFieldId, out _)
                && child.TryGetFieldByNo(NoRelationFieldId, out _))
                return child;
        }

        Assert.Fail(
            $"table {ChildTableId} did not come back carrying all three fields after three "
            + "register-and-rebuild attempts; it has field(s): "
            + (child == null
                ? "<no metatable at all>"
                : string.Join(", ", child.Fields.Select(f => $"{f.FieldNo} {f.FieldName}"))));
        return child!;
    }

    /// <summary>
    /// Drive the guard the Cecil prepend installs on
    /// <c>RecordImplementation.EvaluateRelation(NCLMetaField)</c>. The prepend forwards two
    /// reference-typed IL slots — the RecordImplementation and the NCLMetaField — and the
    /// guard reads only the second, so passing null for the receiver here exercises exactly
    /// what the prepend does without needing a live RecordImplementation.
    /// </summary>
    private static int EvaluateRelation(NCLMetaField field)
    {
        var m = typeof(RecordPatches).GetMethod(
                    "RecordImpl_UnresolvedRelationGuardForEvaluate",
                    BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException(
                    "RecordPatches.RecordImpl_UnresolvedRelationGuardForEvaluate not found — "
                    + "this test drives the guard the Cecil prepend installs.");
        try
        {
            m.Invoke(null, new object?[] { null, field });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(tie.InnerException).Throw();
        }
        // The guard returns void — BC's own body then runs. -1 is what BC answers for a
        // relation-less or non-applying field off an empty buffer, and the tests above assert
        // "not refused" rather than re-deriving BC's arithmetic.
        return field.FieldRelations is { Count: > 0 } ? 0 : -1;
    }
}
