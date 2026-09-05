// Issue #2519. Object Metadata (2000000071) on the runner: where the rows come from when
// there is no application database, and what the columns the runner cannot answer read.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST — AND WHAT IS THEREFORE UNVERIFIED
//   The table's CONTENT is plain BC behaviour and BELONGS upstream. It could not go there.
//   Corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#153 tried and was withdrawn: the
//   corpus app targets Cloud and this table is Scope = OnPrem, so `Record "Object Metadata"`
//   does not compile there (AL0296), and the RecordRef route is refused at RUNTIME by
//   NavRecordRef.CheckIsOpenAllowed on all 8 BC legs of run 33968379281 —
//   "You cannot open record 2000000071 from a RecordRef data type when you are using target
//   Cloud." 2000000071 is in SystemTables.InternalTables, and the escape hatch
//   SystemTables.OnPremSystemTableRecordRefAllowed is only { 2000000187, 2000000188 }.
//
//   SO NO SERVICE TIER HAS CONFIRMED THE ROW SET THIS SUITE OBSERVES. It is derived from
//   Microsoft's own publish-side code (see the C# file's header). Treat the assertions below
//   as pinning what the RUNNER does, which is what a runner suite is for — not as evidence
//   about BC.
//
//   What IS runner-specific: on a real tier those rows exist because publishing wrote them
//   into a SQL table. The runner has no application database and never publishes anything, so
//   it synthesises the row set from BC's OWN
//   Microsoft.Dynamics.Nav.Types.SystemTables.ApplicationDatabaseTables — the same collection
//   Microsoft's CleanupObjectMetadataFromNonApplicationDatabaseTables migration interpolates
//   into its DELETE. "The rows are there with no database behind them" is a claim about the
//   runner, and it is what the first two tests assert.
//
//   And the compiled-metadata payload has no runner source at all. Those columns are left at
//   BC's own default rather than fabricated, which is a DECLARED divergence (docs/limitations.md)
//   — declared precisely because this suite asserts it. Issue #2771 tracks making them refuse
//   by name instead; when that lands, this half of the suite is what tells you the answer moved.
//
// IDS USED BELOW ARE ALL LIVE TABLES ON PURPOSE
//   11 of the 43 ids in SystemTables.ApplicationDatabaseTables are declared
//   ObsoleteState = Removed in System.app (2000000151 among them). Whether real BC publishes a
//   row for those is the one part of the row set that is genuinely open — see the C# header —
//   so this suite asserts only ids that are live table objects on both BC 27.0 and 28.1, and
//   does not encode the open question as settled either way.
codeunit 65541 "OMST Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "OMST Assert";

    [Test]
    procedure RowSet_IsSynthesised_WithNoApplicationDatabase()
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        // The runner has no SQL, no publish step and no restored backup here, yet the rows
        // BC's own application-database table list declares are present. That is the whole
        // fix for #2519: before it, this table was empty and a FindLast raised
        // "There is no Object Metadata within the filter."
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        Assert.IsFalse(ObjectMetadata.IsEmpty(), 'The synthesised row set must not be empty.');
        Assert.IsTrue(ObjectMetadata.FindLast(), 'FindLast over Object Type = Table must succeed.');

        // 2000000400 is the highest id in BC 27/28's ApplicationDatabaseTables, so it is the
        // row FindLast lands on. Asserting the concrete id rather than "some row" is what
        // makes this fail if the synthesis ever falls back to a single placeholder row.
        Assert.AreEqual(
            2000000400, ObjectMetadata."Object ID",
            'FindLast over Object Type = Table must land on the highest application-database table id.');
    end;

    [Test]
    procedure RowSet_TracksBcsOwnApplicationDatabaseTableList()
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        // Four concrete ids BC lists as application-database tables, spread across the range
        // so a synthesis that emitted only its own id, or only a contiguous block, fails here.
        AssertHasRow(2000000001, 'Object');
        AssertHasRow(2000000071, 'Object Metadata itself');
        AssertHasRow(2000000212, 'Application Object Metadata');
        AssertHasRow(2000000400, 'the highest listed id');

        // ...and two BC lists as VIRTUAL system tables, which have no SQL schema in the
        // application database. Without these the test above would also pass against a
        // synthesis that emitted every system table id it could think of.
        AssertHasNoRow(2000000026, 'Integer, a virtual system table');
        AssertHasNoRow(2000000038, 'AllObj, a virtual system table');

        // And an application table, which is not a system table at all.
        AssertHasNoRow(18, 'Customer, an application table');

        // Total is BC's own list, not a hand-picked subset: nothing outside Object Type =
        // Table exists, so the unfiltered count is the Table-filtered count.
        ObjectMetadata.Reset();
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        Assert.AreEqual(
            ObjectMetadata.Count(), CountAllRows(),
            'Every synthesised row must carry Object Type = Table.');
    end;

    [Test]
    procedure MetadataPayloadColumns_ReadBlank_DeclaredDivergence()
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        // On a real tier these carry the output of publishing the system app into the
        // application database. The runner publishes nothing, so it leaves them at BC's own
        // default instead of fabricating a payload. Asserting the exact blanks is what makes
        // that a declared divergence rather than a silent one — see docs/limitations.md.
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        ObjectMetadata.SetRange("Object ID", 2000000071);
        Assert.IsTrue(ObjectMetadata.FindFirst(), 'Object Metadata must have a row for its own table id.');

        Assert.AreEqual(0, ObjectMetadata."Metadata Version", '"Metadata Version" must read 0.');
        Assert.AreEqual('', ObjectMetadata.Hash, '"Hash" must read empty.');
        Assert.AreEqual('', ObjectMetadata."Object Subtype", '"Object Subtype" must read empty.');
        Assert.IsFalse(ObjectMetadata."Has Subscribers", '"Has Subscribers" must read false.');
        Assert.AreEqual(0, ObjectMetadata."Schema Hash", '"Schema Hash" must read 0.');

        ObjectMetadata.CalcFields(Metadata, "User Code", "User AL Code", "Symbol Reference");
        Assert.IsFalse(ObjectMetadata.Metadata.HasValue(), '"Metadata" must carry no payload.');
        Assert.IsFalse(ObjectMetadata."User Code".HasValue(), '"User Code" must carry no payload.');
        Assert.IsFalse(ObjectMetadata."User AL Code".HasValue(), '"User AL Code" must carry no payload.');
        Assert.IsFalse(ObjectMetadata."Symbol Reference".HasValue(), '"Symbol Reference" must carry no payload.');
    end;

    [Test]
    procedure EmitVersion_IsABuildEmitVersionReadFromBc_NotAChosenConstant()
    var
        ObjectMetadata: Record "Object Metadata";
        FirstEmitVersion: Integer;
    begin
        // "Emit Version" is the third primary-key field, so it cannot be left unset. The runner
        // reads NavEnvironment.Instance.EmitVersion rather than choosing a number.
        //
        // BC's emit version is <major><3-digit build counter>: measured off the NavEnvironment
        // constructor in each artifact, BC 27.5 is 27024 and BC 28.1 is 28014. Pinning the exact
        // value would need a per-BC-minor table this suite has no source for, so the assertion is
        // the range every supported minor (27.0-28.4) falls in. That is deliberately narrow
        // enough to fail a chosen constant: 0, 1 and 42 are all outside it. An earlier version of
        // this test asserted only "> 0 and uniform", which a `return 1;` in
        // ReadNavEnvironmentEmitVersion passes -- exactly the mutation tdd.md's "would this pass
        // against a default?" question is asking about.
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        Assert.IsTrue(ObjectMetadata.FindFirst(), 'Object Metadata must not be empty.');
        FirstEmitVersion := ObjectMetadata."Emit Version";

        Assert.IsTrue(
            FirstEmitVersion >= 27000,
            StrSubstNo('Emit Version %1 is below every supported BC build''s emit version (27.0 onwards), so it is not BC''s own value.', FirstEmitVersion));
        Assert.IsTrue(
            FirstEmitVersion < 30000,
            StrSubstNo('Emit Version %1 is above every supported BC build''s emit version, so it is not BC''s own value.', FirstEmitVersion));

        // One process has one emit version, so every row carries it.
        ObjectMetadata.SetFilter("Emit Version", '<>%1', FirstEmitVersion);
        Assert.IsTrue(
            ObjectMetadata.IsEmpty(),
            'Every synthesised row must carry the one emit version this process has.');
    end;

    local procedure AssertHasRow(ObjectId: Integer; Why: Text)
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        ObjectMetadata.SetRange("Object ID", ObjectId);
        Assert.IsFalse(
            ObjectMetadata.IsEmpty(),
            StrSubstNo('Object Metadata must have a row for %1 (%2).', ObjectId, Why));
    end;

    local procedure AssertHasNoRow(ObjectId: Integer; Why: Text)
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        ObjectMetadata.SetRange("Object ID", ObjectId);
        Assert.IsTrue(
            ObjectMetadata.IsEmpty(),
            StrSubstNo('Object Metadata must have no row for %1 (%2).', ObjectId, Why));
    end;

    local procedure CountAllRows(): Integer
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        exit(ObjectMetadata.Count());
    end;
}
