// Proves the virtual Field system table (2000000041) enumerates a table's REAL
// field metadata through the runner's managed find-interception path.
//
// RED (before the fix): Field.SetRange(TableNo,<t>); Field.FindSet() either
// returned zero rows or SIGSEGV'd (exit 139) inside BC's R2R-precompiled native
// InnerFindAsync prologue on the skeleton session. RecoverySolutions'
// "Library - Workflow".EnableWorkflow then threw "There is no Field within the
// filter." in [Setup] for all 34 approval tests.
//
// GREEN (after the fix): for table 2000000041 only, FindAsync is redirected to a
// managed bypass that builds REAL Field rows (one per NCLMetaField) and runs BC's
// own filter/sort engine over them — so the exact EnableWorkflow filter set
// behaves as it does on the service tier.
//
// This bundle defines its OWN table (60601) so the proof needs no Base App: the
// virtual Field provider populates rows for every table in the metadata cache,
// table 60601 included.
codeunit 60602 "VFT Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "VFT Assert";

    SampleTableId: Integer;

    trigger OnRun()
    begin
        SampleTableId := Database::"VFT Sample"; // 60601
    end;

    // The virtual Field table must expose every field we defined — including the
    // BLOB field — when no Type filter is applied. Proves real metadata, not a
    // single fabricated row.
    [Test]
    procedure OwnTable_EnumeratesAllDefinedFields()
    var
        FieldRec: Record "Field";
        SawDescription: Boolean;
        SawAmount: Boolean;
        SawBlob: Boolean;
        Count: Integer;
    begin
        FieldRec.SetRange(TableNo, Database::"VFT Sample");
        Count := 0;
        if FieldRec.FindSet() then
            repeat
                Count += 1;
                if FieldRec."No." = 2 then
                    SawDescription := true;
                if FieldRec."No." = 3 then
                    SawAmount := true;
                if FieldRec."No." = 10 then
                    SawBlob := true;
            until FieldRec.Next() = 0;

        Assert.IsTrue(Count >= 4, 'VFT Sample must expose at least its 4 defined fields through the virtual Field table');
        Assert.IsTrue(SawDescription, 'Field 2 (Description) must be enumerated');
        Assert.IsTrue(SawAmount, 'Field 3 (Amount) must be enumerated');
        Assert.IsTrue(SawBlob, 'Field 10 (Blob Data) must be enumerated when no Type filter is applied');
    end;

    // Concrete positive: field 2 has the real No./TableNo/Name/Type — proves the
    // rows carry genuine NCLMetaField metadata, not placeholders.
    [Test]
    procedure Field2_HasRealNameAndType()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.SetRange(TableNo, Database::"VFT Sample");
        FieldRec.SetRange("No.", 2);
        Assert.IsTrue(FieldRec.FindFirst(), 'Field 2 of VFT Sample must exist in the virtual Field table');
        Assert.AreEqual(2, FieldRec."No.", 'Field No. must be 2');
        Assert.AreEqual(Database::"VFT Sample", FieldRec.TableNo, 'TableNo must be the VFT Sample table');
        Assert.AreEqualText('Description', FieldRec.FieldName, 'Field 2 name must be "Description"');
        Assert.IsTrue(FieldRec.Type = FieldRec.Type::Text, 'Field 2 type must be Text');
    end;

    // The exact EnableWorkflow filter set (No.<>1, Type<>BLOB, ObsoleteState<>Removed)
    // must drop the primary-key field and the BLOB field while keeping the normal
    // fields — exactly as BC's filter engine does on the service tier. This is the
    // precise pattern that was failing in RecoverySolutions.
    [Test]
    procedure EnableWorkflowFilterSet_ExcludesPkAndBlob_KeepsNormalFields()
    var
        FieldRec: Record "Field";
        SawField1: Boolean;
        SawBlob: Boolean;
        SawDescription: Boolean;
        SawAmount: Boolean;
        Count: Integer;
    begin
        FieldRec.SetRange(TableNo, Database::"VFT Sample");
        FieldRec.SetFilter("No.", '<>%1', 1);
        FieldRec.SetFilter(Type, '<>%1', FieldRec.Type::BLOB);
        FieldRec.SetFilter(ObsoleteState, '<>%1', FieldRec.ObsoleteState::Removed);

        Count := 0;
        if FieldRec.FindSet() then
            repeat
                Count += 1;
                if FieldRec."No." = 1 then
                    SawField1 := true;
                if FieldRec.Type = FieldRec.Type::BLOB then
                    SawBlob := true;
                if FieldRec."No." = 2 then
                    SawDescription := true;
                if FieldRec."No." = 3 then
                    SawAmount := true;
            until FieldRec.Next() = 0;

        Assert.IsTrue(Count > 0, 'EnableWorkflow filter set must return a non-empty Field set (the gap returned zero)');
        Assert.IsFalse(SawField1, 'Primary-key field 1 must be filtered out by No.<>1');
        Assert.IsFalse(SawBlob, 'BLOB field must be filtered out by Type<>BLOB');
        Assert.IsTrue(SawDescription, 'Normal field 2 (Description) must survive the filter');
        Assert.IsTrue(SawAmount, 'Normal field 3 (Amount) must survive the filter');
    end;

    // ── Issue #2792: the same table, asked three different ways ─────────────────────
    //
    // The runner serves this table from an in-memory store, so it has to BUILD a table's field
    // rows before it can answer for that table, and it builds them on demand from the table id
    // the request names. FOUR DataAccess request paths reach the table, each carrying that id
    // differently:
    //
    //   Find / FindSet / FindFirst -> InnerFindAsync(FindCacheRequest)      id in a TableNo filter
    //   Count                      -> CountAsync(CountCacheRequest)         id in a TableNo filter
    //   IsEmpty                    -> ExistsAsync(ExistsCacheRequest)       id in a TableNo filter
    //   Get(TableNo, No.)          -> InternalTryGetByPrimaryKeyAsync(...)  id in the RecordId
    //
    // Only the find path ran the populate, so Count() answered 0, IsEmpty() answered true and
    // Get() answered false for a table nothing had opened, while FindSet() over the same filter
    // answered with its real rows. Every wrong answer is the quiet kind: 0 rows, "the set is
    // empty" and "no such field" are exactly what an empty table looks like. Same shape as
    // #2648 (Date) and #2504 (Aggregate Permission Set), which fixed two of these methods for
    // their own tables and left this one behind.
    //
    // IsEmpty() is a fourth path and not a spelling of Count(): RecordImplementation.IsEmptyAsync
    // calls its own ExistsAsync rather than counting (decompiled from Ncl.dll). It is asserted
    // separately below for that reason — assuming it shared Count()'s implementation is what
    // would have left it broken.
    //
    // These three tests exist only because the runner materialises at all — on a service tier
    // the table is computed per request and there is nothing to materialise. What the rows
    // themselves say is plain BC behaviour, pinned upstream in the al-language corpus; it is
    // named here only because a proof that the populate ran has to say what it produced.
    //
    // WHY A BASE APPLICATION TABLE, AND A DIFFERENT ONE PER TEST. The precondition is "the
    // runner has not built this table's metadata yet". Every table declared in AL source the
    // runner parses — this bundle's own "VFT Sample" included — is in the metadata cache before
    // any test runs, so it can never show the gap. A precompiled Base Application table is only
    // built when something asks for it; measured over a full tests/runner-extras run, 193 real
    // tables were in the cache when this codeunit executed and none of the three below was one
    // of them. And the populate is idempotent and process-wide, so the first request of any
    // kind warms that table for every later one — hence one table per test.

    // Count() must build the rows the same way a find does. RED: 0.
    [Test]
    procedure Count_TableNotYetMaterialised_CountsRealFields()
    var
        FieldRec: Record "Field";
    begin
        // [GIVEN] a filter naming Base Application "Value Entry" (5802), which nothing in this
        //         run has opened as a Record
        FieldRec.SetRange(TableNo, 5802);
        FieldRec.SetRange("No.", 1, 2);

        // [THEN] Count() sees its first two real fields, not an empty store
        Assert.AreEqual(2, FieldRec.Count(), 'Count() must see fields 1 and 2 of Value Entry (5802)');
    end;

    // IsEmpty() takes the same count path and had the same wrong answer. RED: true.
    // A different table, because 5802 is warm once the test above has run.
    [Test]
    procedure IsEmpty_TableNotYetMaterialised_IsFalse()
    var
        FieldRec: Record "Field";
    begin
        // [GIVEN] Base Application "Bank Account" (270), likewise untouched
        FieldRec.SetRange(TableNo, 270);

        // [THEN] the table has fields, so it is not empty
        Assert.IsFalse(FieldRec.IsEmpty(), 'IsEmpty() must be false for a table that has fields');

        // [AND] Count() agrees with IsEmpty() about the same filter
        FieldRec.SetRange("No.", 1, 2);
        Assert.AreEqual(2, FieldRec.Count(), 'Count() must see fields 1 and 2 of Bank Account (270)');
    end;

    // A keyed Get carries the table id in its RecordId, not in a filter, and reaches neither of
    // the paths above. RED: Get returns false, so FieldName reads blank.
    [Test]
    procedure Get_ByPrimaryKey_TableNotYetMaterialised_ReturnsRealField()
    var
        FieldRec: Record "Field";
    begin
        // [WHEN] a full-primary-key Get is the first request made for Base Application
        //        "Dimension Value" (349)
        Assert.IsTrue(FieldRec.Get(349, 1), 'Field.Get must find field 1 of Dimension Value (349)');

        // [THEN] it is that table's real field 1, not a placeholder
        Assert.AreEqual(349, FieldRec.TableNo, 'TableNo must be Dimension Value (349)');
        Assert.AreEqual(1, FieldRec."No.", 'Field No. must be 1');
        Assert.AreEqualText('Dimension Code', FieldRec.FieldName, 'Field 1 must be named "Dimension Code"');
        Assert.IsTrue(FieldRec.Type = FieldRec.Type::Code, 'Field 1 must have type Code');

        // [AND] a field number the table does not declare stays absent — the populate builds
        //       real metadata and never fabricates a row to satisfy the key.
        Assert.IsFalse(FieldRec.Get(349, 999999), 'Field 999999 of Dimension Value must not exist');
    end;

    // The four paths must agree on one table, so a regression in any one of them cannot hide
    // behind the other three. The keyed Get goes first, while the table is still cold.
    [Test]
    procedure AllFourPaths_AgreeOnTheSameTable()
    var
        FieldRec: Record "Field";
        Rows: Integer;
    begin
        // [GIVEN] Base Application "Avg. Cost Adjmt. Entry Point" (5804), untouched, read by key
        Assert.IsTrue(FieldRec.Get(5804, 1), 'Field.Get must find field 1 of table 5804');

        // [WHEN] the same table is put to the count path and then the find path
        FieldRec.Reset();
        FieldRec.SetRange(TableNo, 5804);
        Assert.IsFalse(FieldRec.IsEmpty(), 'IsEmpty() must be false for table 5804');

        FieldRec.SetRange("No.", 1, 2);
        Assert.AreEqual(2, FieldRec.Count(), 'Count() must see fields 1 and 2 of table 5804');

        if FieldRec.FindSet() then
            repeat
                Rows += 1;
            until FieldRec.Next() = 0;

        // [THEN] FindSet enumerates exactly what Count counted
        Assert.AreEqual(2, Rows, 'FindSet() must enumerate the same 2 fields Count() counted');
    end;

    // Negative for the three paths added above: an id that is not a table must stay empty on
    // every one of them. The on-demand populate must not invent a table because a request
    // named one.
    [Test]
    procedure NonExistentTable_StaysEmptyOnCountIsEmptyAndGet()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.SetRange(TableNo, 1999999);
        Assert.AreEqual(0, FieldRec.Count(), 'Count() must be 0 for a table id that does not exist');
        Assert.IsTrue(FieldRec.IsEmpty(), 'IsEmpty() must be true for a table id that does not exist');

        FieldRec.Reset();
        Assert.IsFalse(FieldRec.Get(1999999, 1), 'Get() must fail for a table id that does not exist');
    end;

    // Negative: a non-existent table must yield zero rows — the provider builds
    // rows from real metadata only and must never fabricate a row.
    [Test]
    procedure NonExistentTable_YieldsNoFields()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.SetRange(TableNo, 1999999); // not a real table
        Assert.IsFalse(FieldRec.FindFirst(), 'A non-existent table must yield no Field rows');
    end;
}
