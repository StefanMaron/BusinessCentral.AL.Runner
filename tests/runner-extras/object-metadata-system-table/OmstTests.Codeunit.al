// Issue #2519. Object Metadata (2000000071) on the runner: where the rows come from when
// there is no application database, and what the columns the runner cannot answer read.
//
// WHY THIS IS A RUNNER TEST AND NOT A CORPUS TEST — AND WHAT A TIER HAS SINCE CONFIRMED
//   The table's CONTENT is plain BC behaviour and belongs upstream. This header used to say
//   "NO SERVICE TIER HAS CONFIRMED THE ROW SET THIS SUITE OBSERVES", because the first attempt
//   to put it upstream failed: corpus PR StefanMaron/BusinessCentral.AL.Language.Tests#153 was
//   withdrawn after all 8 BC legs of run 33968379281 refused the only route a Cloud-target app
//   has — "You cannot open record 2000000071 from a RecordRef data type when you are using
//   target Cloud" (NavRecordRef.CheckIsOpenAllowed; 2000000071 is in
//   SystemTables.InternalTables, and the escape hatch
//   SystemTables.OnPremSystemTableRecordRefAllowed is only { 2000000187, 2000000188 }).
//
//   THAT IS NO LONGER TRUE, and leaving it here was how two neighbouring stale claims survived
//   long enough to be contradicted (AlRunner#3066). Corpus PR #179 added a Target = OnPrem app
//   — the compilation target is what decides both refusals, and nothing else — and
//   tests/al-language-onprem/record/TestObjectMetadataSystemTable.al now measures the row set
//   on all eight OnPrem legs, BC 27.0 through 28.4: 43 rows under Object Type = Table, one per
//   id on Microsoft's application-database table list, INCLUDING the ObsoleteState = Removed
//   and Pending ids; no row for a virtual system table or an ordinary application table;
//   FindLast landing on 2000000400. This repository's own corpus leg runs that file against
//   the runner, so the claim is adjudicated on both sides.
//
//   What is left for this suite is the RUNNER's side of it, which no tier can see.
//
//   What IS runner-specific: on a real tier those rows exist because publishing wrote them
//   into a SQL table. The runner has no application database and never publishes anything, so
//   it synthesises the row set from BC's OWN
//   Microsoft.Dynamics.Nav.Types.SystemTables.ApplicationDatabaseTables — the same collection
//   Microsoft's CleanupObjectMetadataFromNonApplicationDatabaseTables migration interpolates
//   into its DELETE. "The rows are there with no database behind them" is a claim about the
//   runner, and it is what the first two tests assert.
//
//   And the compiled-metadata payload has no runner source at all. Since #2771 those nine
//   columns REFUSE BY NAME when read, rather than handing back BC's default — a 0-byte BLOB,
//   0, '' or false, every one of which is a legitimate value for the column and therefore
//   indistinguishable from "the runner has no source for this". Two of the tests below assert
//   the refusal, split by which of the two seams catches it (the runner's own blob load for the
//   BLOBs, NavRecord.GetFieldValueSafe for the scalars), and a third asserts that refusing one
//   COLUMN did not take the four request paths down with it — the trap #2519 named.
//
// IDS USED BELOW ARE ALL LIVE TABLES ON PURPOSE
//   11 of the 43 ids in SystemTables.ApplicationDatabaseTables are declared
//   ObsoleteState = Removed in System.app (2000000151 among them). When this suite was written
//   it was open whether real BC publishes a row for those, so it asserts only ids that are live
//   table objects on both BC 27.0 and 28.1. Corpus #179 has since settled it — a tier does
//   publish rows for the Removed ids, and upstream's
//   ObjectMetadata_Find_ObsoleteRemovedId_ReturnsARow pins 2000000151 specifically. This suite
//   is left narrower on purpose: the wider claim is upstream's now, and duplicating it here
//   would put a BC assertion back into a runner-local suite.
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
        AssertHasRow(2000000212, 'Installed Application');
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
    procedure MetadataPayloadBlobs_RefuseByName_RatherThanReadingAnEmptyPayload()
    var
        ObjectMetadata: Record "Object Metadata";
    begin
        // On a real tier these four BLOBs carry the output of publishing the system app into
        // the application database. The runner publishes nothing, so it has no payload — and
        // until #2771 it handed back BC's own default, a 0-byte BLOB. That is the silent kind
        // of wrong answer .claude/rules/loud-failures.md forbids: `HasValue()` reads false and
        // `CreateInStream` yields an empty stream, both of which are exactly what a legitimately
        // empty BLOB looks like, so nothing complains.
        //
        // Each arm names its OWN column. A refusal that named only the table would pass a
        // single assertion and tell a developer nothing about which read to stop making.
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        ObjectMetadata.SetRange("Object ID", 2000000071);
        Assert.IsTrue(ObjectMetadata.FindFirst(), 'Object Metadata must have a row for its own table id.');

        asserterror ObjectMetadata.CalcFields(Metadata);
        Assert.ExpectedError('out-of-scope: Object Metadata."Metadata" (system table 2000000071)');
        Assert.ExpectedError('object-metadata-payload');
        Assert.NotExpectedError('Object reference not set');

        asserterror ObjectMetadata.CalcFields("User Code");
        Assert.ExpectedError('out-of-scope: Object Metadata."User Code" (system table 2000000071)');

        asserterror ObjectMetadata.CalcFields("User AL Code");
        Assert.ExpectedError('out-of-scope: Object Metadata."User AL Code" (system table 2000000071)');

        asserterror ObjectMetadata.CalcFields("Symbol Reference");
        Assert.ExpectedError('out-of-scope: Object Metadata."Symbol Reference" (system table 2000000071)');

        // A CalcFields naming several refusing columns at once still refuses, and names one of
        // them rather than dying somewhere unattributable.
        asserterror ObjectMetadata.CalcFields(Metadata, "User Code", "User AL Code", "Symbol Reference");
        Assert.ExpectedError('out-of-scope: Object Metadata.');
        Assert.ExpectedError('(system table 2000000071)');
    end;

    [Test]
    procedure MetadataPayloadScalars_RefuseByName_RatherThanReadingBlank()
    var
        ObjectMetadata: Record "Object Metadata";
        Sink: Text;
    begin
        // The five non-BLOB payload columns had the same defect in a quieter form: 0, '' and
        // false are all ordinary values, so `if ObjectMetadata."Has Subscribers" then` simply
        // took the wrong branch. Read through Format() so the assertion does not depend on
        // this test guessing each column's declared AL type.
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        ObjectMetadata.SetRange("Object ID", 2000000071);
        Assert.IsTrue(ObjectMetadata.FindFirst(), 'Object Metadata must have a row for its own table id.');

        asserterror Sink := Format(ObjectMetadata."Metadata Version");
        Assert.ExpectedError('out-of-scope: Object Metadata."Metadata Version" (system table 2000000071)');
        Assert.ExpectedError('object-metadata-payload');

        asserterror Sink := Format(ObjectMetadata.Hash);
        Assert.ExpectedError('out-of-scope: Object Metadata."Hash" (system table 2000000071)');

        asserterror Sink := Format(ObjectMetadata."Object Subtype");
        Assert.ExpectedError('out-of-scope: Object Metadata."Object Subtype" (system table 2000000071)');

        asserterror Sink := Format(ObjectMetadata."Has Subscribers");
        Assert.ExpectedError('out-of-scope: Object Metadata."Has Subscribers" (system table 2000000071)');

        asserterror Sink := Format(ObjectMetadata."Schema Hash");
        Assert.ExpectedError('out-of-scope: Object Metadata."Schema Hash" (system table 2000000071)');
    end;

    [Test]
    procedure RefusingAPayloadColumn_LeavesAllFourRequestPathsWorking()
    var
        ObjectMetadata: Record "Object Metadata";
        EmitVersion: Integer;
    begin
        // The trap #2519 named: throwing at ROW-BUILD time would refuse the payload columns and
        // take out FindSet / Count / Get with them, which is the original bug wearing a new hat.
        // The refusal is on the READ of one column, so every request path still answers, and the
        // three columns that have a real source still read.
        //
        // All FOUR DataAccess request paths, because they are four independent implementations:
        // RecordImplementation.IsEmptyAsync calls its own ExistsAsync and never routes through
        // CountAsync, so a fix verified on find, count and keyed Get can still be broken here.

        // find — InnerFindAsync
        ObjectMetadata.SetRange("Object Type", ObjectMetadata."Object Type"::Table);
        ObjectMetadata.SetRange("Object ID", 2000000071);
        Assert.IsTrue(ObjectMetadata.FindFirst(), 'FindFirst must still answer after the refusal.');
        Assert.AreEqual(2000000071, ObjectMetadata."Object ID", 'The found row must be the one asked for.');
        EmitVersion := ObjectMetadata."Emit Version";
        Assert.IsTrue(EmitVersion >= 27000, 'A column with a real source must still read.');

        // count — CountAsync
        Assert.AreEqual(1, ObjectMetadata.Count(), 'Count must still answer after the refusal.');

        // IsEmpty — ExistsAsync, a fourth path and not a spelling of Count
        Assert.IsFalse(ObjectMetadata.IsEmpty(), 'IsEmpty must still answer after the refusal.');

        // keyed Get — InternalTryGetByPrimaryKeyAsync
        Clear(ObjectMetadata);
        Assert.IsTrue(
            ObjectMetadata.Get(ObjectMetadata."Object Type"::Table, 2000000071, EmitVersion),
            'A keyed Get must still answer after the refusal.');
        Assert.AreEqual(2000000071, ObjectMetadata."Object ID", 'The Get row must be the one asked for.');

        // ...and the negative twin on every path, so none of them passes vacuously.
        ObjectMetadata.Reset();
        ObjectMetadata.SetRange("Object ID", 18);
        Assert.AreEqual(0, ObjectMetadata.Count(), 'Count must still refuse an id with no row.');
        Assert.IsTrue(ObjectMetadata.IsEmpty(), 'IsEmpty must still refuse an id with no row.');
        Assert.IsFalse(ObjectMetadata.FindFirst(), 'FindFirst must still refuse an id with no row.');
        Assert.IsFalse(
            ObjectMetadata.Get(ObjectMetadata."Object Type"::Table, 18, EmitVersion),
            'A keyed Get must still refuse an id with no row.');
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
