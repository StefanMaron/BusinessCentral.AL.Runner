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
