// TableRelationOntoSystemRowVersionTests — issue #3325.
//
// WHAT IS PINNED, AND WHAT DELIBERATELY IS NOT
// --------------------------------------------
// This file pins ONE runner-side mechanical fact: the `SourceFieldId` the runner writes into
// the built `NCLMetaFieldRelation` for a `TableRelation = <Table>.SystemRowVersion`, and that
// it is the SAME id a relation naming no field at all produces.
//
// It does NOT assert what real BC does with such a relation. That is plain BC behaviour, it is
// the actual open question in #3325, and it is asked upstream where a service tier answers it:
// corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#324 adds
// `Record_Validate_TableRelationOntoSystemRowVersion` to codeunit 60818. Duplicating that
// claim here would give it a green tick from the runner agreeing with itself, which is exactly
// what `.claude/rules/bc-behavior-tests-go-upstream.md` exists to prevent.
//
// THE MECHANISM
// -------------
// `SystemRowVersionParsedField` is `new ParsedField(0, "SystemRowVersion", "BigInteger", 0, …)`
// — field id 0, because BC gives the rowversion column id 0 under its metadata name
// `timestamp`. `BuildMetaFieldRelations` initialises its local `fieldId` to 0 and overwrites it
// only when `TryResolveTableFieldByName` succeeds. So two different AL spellings converge on
// one value:
//
//     TableRelation = "TRSRV Row".SystemRowVersion     -> resolved,   fieldId = 0
//     TableRelation = "TRSRV Row"                      -> no field,   fieldId = 0
//
// and from `NCLMetaFieldRelation` onward nothing can tell them apart. BC reads
// `SourceFieldId = 0` as "relate to the related table's primary key", which is why the runner
// today refuses a rowversion that genuinely exists on a row — the observation #3325 was filed
// on. That collision is the runner's own structure, visible without asking a service tier
// anything, and it is what the assertions below hold.
//
// WHY IT IS WORTH A TEST WHATEVER THE CORPUS ANSWERS
// --------------------------------------------------
// Both possible verdicts land here. If BC relates to the rowversion column, the runner has to
// stop encoding this target as 0 and this test is the RED that the fix turns; if BC relates to
// the primary key, the runner is already right and this test is what stops a later change to
// `SystemRowVersionParsedField`'s id — a plausible edit, since 0 looks like a placeholder —
// from silently moving the relation off the shape BC adjudicated.
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class TableRelationOntoSystemRowVersionTests : IDisposable
{
    private readonly BcEngineFixture _engine;
    private readonly string _root;

    // Process-wide unique among AlRunner.Tests statics: these land in the same static
    // _parsedTables / _metaTableCache the whole assembly shares, so a duplicate id does not
    // fail loudly — it hands back the OTHER file's table. Same hazard
    // TableRelationUnresolvedRefusalTests documents at its own id constants.
    private const int RowTableId = 94240;
    private const int RelTableId = 94241;

    private const int RowVersionRelationFieldId = 2;
    private const int SystemIdRelationFieldId = 3;
    private const int WholeTableRelationFieldId = 4;
    private const int NamedFieldRelationFieldId = 5;

    public TableRelationOntoSystemRowVersionTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-3325-rowversion-relation");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [SkippableFact]
    public void RelationOntoSystemRowVersion_IsCarried_AndTargetsTheRelatedTable()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var rel = BuildRelTable();

        Assert.True(rel.TryGetFieldByNo(RowVersionRelationFieldId, out var rowVersionRef));

        // The relation survives the builder at all. Before #3307's resolver change
        // `SystemRowVersion` did not resolve, `BuildMetaFieldRelations` refused the whole
        // property, and this collection was empty — which every consumer reads as "this field
        // declares no TableRelation". Asserting non-empty is what stops a regression to that.
        Assert.NotNull(rowVersionRef.FieldRelations);
        var arm = Assert.Single(rowVersionRef.FieldRelations!);

        // It names the right table concretely. A relation carried with the target lost would
        // still leave FieldRef.Relation answering 0.
        Assert.Equal(RowTableId, arm.SourceTableId);
        Assert.Equal(RelTableId, arm.ReferencingTableId);
        Assert.Equal(RowVersionRelationFieldId, arm.ReferencingFieldId);
    }

    [SkippableFact]
    public void SystemRowVersionTarget_EncodesAsFieldZero_IndistinguishableFromAWholeTableRelation()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var rel = BuildRelTable();

        Assert.True(rel.TryGetFieldByNo(RowVersionRelationFieldId, out var rowVersionRef));
        Assert.True(rel.TryGetFieldByNo(WholeTableRelationFieldId, out var wholeTableRef));

        var rowVersionArm = Assert.Single(rowVersionRef.FieldRelations!);
        var wholeTableArm = Assert.Single(wholeTableRef.FieldRelations!);

        // The claim, stated as a concrete id rather than as "they are equal": naming
        // SystemRowVersion as the relation target produces SourceFieldId 0.
        Assert.Equal(0, rowVersionArm.SourceFieldId);

        // And 0 is what a relation naming NO field produces, so BC cannot tell the two apart.
        // This is the collision #3325 is about, and it is the reason the runner refuses a
        // rowversion that exists: BC reads 0 as "the related table's primary key".
        Assert.Equal(0, wholeTableArm.SourceFieldId);
        Assert.Equal(rowVersionArm.SourceFieldId, wholeTableArm.SourceFieldId);
    }

    [SkippableFact]
    public void OrdinaryAndSystemIdTargets_EncodeAsTheirOwnNonZeroIds()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var rel = BuildRelTable();

        // The discriminating control, and the half that makes the assertions above mean
        // something. If the builder encoded EVERY target as 0 — a resolver that silently
        // failed, say — the two "SourceFieldId is 0" assertions above would pass for a reason
        // that has nothing to do with SystemRowVersion. These two must be non-zero and must be
        // their own distinct ids.
        Assert.True(rel.TryGetFieldByNo(NamedFieldRelationFieldId, out var namedRef));
        var namedArm = Assert.Single(namedRef.FieldRelations!);
        Assert.Equal(1, namedArm.SourceFieldId);

        // SystemId is the sibling shape the corpus already pins on a service tier
        // (Record_Validate_TableRelationOntoSystemId, corpus codeunit 60818). It lives in the
        // 2000000000-2000000004 block rather than at 0, which is precisely why it does NOT
        // collide with a whole-table relation and SystemRowVersion does.
        Assert.True(rel.TryGetFieldByNo(SystemIdRelationFieldId, out var sysIdRef));
        var sysIdArm = Assert.Single(sysIdRef.FieldRelations!);
        Assert.Equal(2000000000, sysIdArm.SourceFieldId);
        Assert.NotEqual(0, sysIdArm.SourceFieldId);
    }

    // ── plumbing ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the referencing table's real NCLMetaTable from AL source, through the same entry
    /// point Validate and FieldRef.Relation reach. Four relation fields: onto SystemRowVersion
    /// (the subject), onto SystemId (the sibling that resolves to a non-zero id), onto the
    /// whole table with no field named (the shape SystemRowVersion collides with), and onto an
    /// ordinary declared field (the control that proves the builder encodes real ids at all).
    /// </summary>
    private NCLMetaTable BuildRelTable()
    {
        var srcDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "Row.al"), $$"""
            table {{RowTableId}} "TRSRV Row"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "Entry No."; Integer) { }
                    field(2; "Name"; Text[30]) { }
                }
                keys { key(PK; "Entry No.") { Clustered = true; } }
            }
            """);
        File.WriteAllText(Path.Combine(srcDir, "Rel.al"), $$"""
            table {{RelTableId}} "TRSRV Rel"
            {
                DataClassification = CustomerContent;
                fields
                {
                    field(1; "Entry No."; Integer) { }
                    field({{RowVersionRelationFieldId}}; "Row Ver Ref"; BigInteger)
                    {
                        TableRelation = "TRSRV Row".SystemRowVersion;
                    }
                    field({{SystemIdRelationFieldId}}; "Row Sys Id Ref"; Guid)
                    {
                        TableRelation = "TRSRV Row".SystemId;
                    }
                    field({{WholeTableRelationFieldId}}; "Row Ref"; Integer)
                    {
                        TableRelation = "TRSRV Row";
                    }
                    field({{NamedFieldRelationFieldId}}; "Row Entry Ref"; Integer)
                    {
                        TableRelation = "TRSRV Row"."Entry No.";
                    }
                }
                keys { key(PK; "Entry No.") { Clustered = true; } }
            }
            """);
        var skeleton = AlRunner.BcRuntime.SkeletonNCLMetadata;
        Assert.NotNull(skeleton);

        // Registered and built up to three times, because `_parsedTables` and the metatable
        // cache are process-wide and another collection may call ResetForReload between the
        // parse and the build. Same recovery TableRelationUnresolvedRefusalTests documents.
        NCLMetaTable? rel = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            RecordPatches.AddSourceDir(srcDir);
            rel = RecordPatches.EnsureTableInMetadataCache(RelTableId)
                  ?? RecordPatches.NCLMetadata_GetMetaTableById(skeleton!, RelTableId, false, 0);
            if (rel != null
                && rel.TryGetFieldByNo(RowVersionRelationFieldId, out _)
                && rel.TryGetFieldByNo(SystemIdRelationFieldId, out _)
                && rel.TryGetFieldByNo(WholeTableRelationFieldId, out _)
                && rel.TryGetFieldByNo(NamedFieldRelationFieldId, out _))
                return rel;
        }

        Assert.Fail(
            $"table {RelTableId} did not come back carrying all four fields after three "
            + "register-and-rebuild attempts; it has field(s): "
            + (rel == null
                ? "<no metatable at all>"
                : string.Join(", ", rel.Fields.Select(f => $"{f.FieldNo} {f.FieldName}"))));
        return rel!;
    }
}
