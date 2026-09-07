// Issue #3286 — a TableRelation a SOURCE-PARSED tableextension declares on a PRECOMPILED base
// table must reach the extended table's metadata, so Validate enforces it.
//
// RUNNER-MECHANISM claim. That Validate enforces a tableextension-contributed TableRelation is
// plain BC behaviour, and it is pinned upstream twice — corpus codeunit 60827 for a relation a
// PRECOMPILED tableextension contributes (corpus #207, the case PR #3197 fixed), and corpus
// codeunit 60407 for one the corpus itself DECLARES on a Base Application table (corpus #222).
// Neither can reach the crossing this bundle pins.
//
// THE CROSSING. Two independent readers produce a field's RelationArms:
//
//   * BcAppSymbolCache.TryParseTableExtensionSymbol — a PRECOMPILED tableextension's fields,
//     re-parsed out of the package's SymbolReference.json. This is the reader #3177/#3197
//     fixed; before that fix it dropped TableRelation for all 261 relation-bearing extension
//     fields in the platform packages.
//   * RecordPatches.AlSourceParser.ParseFieldSyntax — a SOURCE tableextension's fields.
//
// Both feed RecordPatches.MergeExtensionFields, which grafts the fields onto the extended
// table. When the extended table is PRECOMPILED, the resulting metatable is built from BC's own
// metadata with these parsed fields merged in, and RelationArms has to survive that graft.
// Nothing asserted that it does: every corpus bundle is one app compiled as a whole, so a
// corpus test cannot separate the two readers, and the runner-extras suite only covered the
// precompiled reader (tests/runner-extras/precompiled-table-relation, #2528/#3197).
//
// WHY THIS BUNDLE EXISTS RATHER THAN A FIX. #3286 was filed as the source-parsed counterpart of
// #3177, reporting that corpus codeunit 60827 fails on the runner. Measured on this tree, that
// codeunit's subject — Customer 5900 "Service Zone Code" — is contributed by tableextension
// 6450 "Serv. Customer" shipped INSIDE the Base Application package, so it exercises the
// PRECOMPILED reader, and all five of its tests pass here. Reverting only #3197's
// TryParseRelationArmsText call turns exactly that one test red and leaves every source-parsed
// probe green, which is what identifies the reader. The source-parsed path was already correct;
// what it lacked was a test, so a regression in it would have been silent in both suites.
codeunit 65726 "STR Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "STR Assert";

    [Test]
    procedure SourceTableExtRelation_OnPrecompiledBase_NoRelatedRow_Throws()
    // CLAIM: Validate refuses a value with no row in the related table, for a relation a
    // source-parsed tableextension declared on a precompiled base table.
    var
        Job: Record Job;
    begin
        Initialize();

        // [GIVEN] No "STR Related" row carries this code.
        Clear(Job);
        Job.Init();

        // [WHEN/THEN] Validating the extension-declared field with it is refused.
        asserterror Job.Validate("STR Ext Rel Code", 'STRREL1');
        Assert.ExpectedError('cannot be found in the related table');
    end;

    [Test]
    procedure SourceTableExtRelation_OnPrecompiledBase_RelatedRowExists_AssignsTheValue()
    // CLAIM: the SAME value the test above refused is accepted once the related row exists, so
    // the refusal was the relation check and not a blanket rejection — and the relation
    // resolved to the RIGHT related table, since a row anywhere else would not rescue it.
    var
        Job: Record Job;
    begin
        Initialize();

        // [GIVEN] A "STR Related" row with exactly the code the previous test was refused.
        InsertRelated('STRREL1');

        // [WHEN] The extension-declared field is validated with it.
        Clear(Job);
        Job.Init();
        Job.Validate("STR Ext Rel Code", 'STRREL1');

        // [THEN] Validate succeeds and the field holds the value.
        Assert.AreEqual(
            'STRREL1', Job."STR Ext Rel Code",
            'Validate must accept a value that exists in the table the source-parsed ' +
            'tableextension''s TableRelation points at, and store it');
    end;

    [Test]
    procedure SourceTableExtRelation_ValidateTableRelationFalse_IsNotChecked()
    // CLAIM: the SECOND property survives the graft too. ValidateTableRelation = false turns the
    // CHECK off while leaving the relation readable, so this field takes the very value the
    // subject was refused. A change that switched relation checking on wholesale — rather than
    // reading both properties — makes this refuse a value real BC accepts.
    var
        Job: Record Job;
    begin
        Initialize();

        // [GIVEN] No "STR Related" row carries this code.
        Clear(Job);
        Job.Init();

        // [WHEN] The ValidateTableRelation = false field is validated with it.
        Job.Validate("STR Ext Rel No Validate", 'STRREL1');

        // [THEN] Validate succeeds — the relation is present but not enforced.
        Assert.AreEqual(
            'STRREL1', Job."STR Ext Rel No Validate",
            'ValidateTableRelation = false must suppress the check on a source-parsed ' +
            'tableextension field, not the relation itself');
    end;

    [Test]
    procedure SourceTableExtRelation_FieldWithNoRelation_SameValue_IsAccepted()
    // CLAIM: control — a field the SAME extension adds with NO TableRelation takes that very
    // value. So the refusal in the first test is the relation, not a blanket refusal of writes
    // to fields a source-parsed tableextension added.
    var
        Job: Record Job;
    begin
        Initialize();

        // [GIVEN] No "STR Related" row carries this code.
        Clear(Job);
        Job.Init();

        // [WHEN] The relation-less field of the same extension is validated with it.
        Job.Validate("STR Ext No Rel", 'STRREL1');

        // [THEN] Validate succeeds — there is no relation to check.
        Assert.AreEqual(
            'STRREL1', Job."STR Ext No Rel",
            'A source-parsed tableextension field with no TableRelation must accept any ' +
            'value of its type');
    end;

    [Test]
    procedure SourceTableExtRelation_DirectAssignment_IsNotChecked()
    // CLAIM: a direct assignment bypasses the relation entirely, so the refusal in the first
    // test is Validate's relation check — not the value, and not the field.
    var
        Job: Record Job;
    begin
        Initialize();

        // [GIVEN] No "STR Related" row carries this code.
        Clear(Job);
        Job.Init();

        // [WHEN] The value is assigned directly rather than validated.
        Job."STR Ext Rel Code" := 'STRREL1';

        // [THEN] The assignment stands — no relation check ran.
        Assert.AreEqual(
            'STRREL1', Job."STR Ext Rel Code",
            'A direct assignment must not consult the source-parsed tableextension''s ' +
            'TableRelation');
    end;

    local procedure Initialize()
    var
        Related: Record "STR Related";
    begin
        // "STR Related" belongs to this bundle alone, so a blanket DeleteAll is safe here —
        // unlike the corpus counterpart, whose related table is shared with other codeunits.
        // No Job row is ever written (Init() plus Validate() is the whole scenario), so none
        // has to be removed.
        Related.Reset();
        Related.DeleteAll(false);
    end;

    local procedure InsertRelated(RelatedCode: Code[20])
    var
        Related: Record "STR Related";
    begin
        Related.Init();
        Related."Code" := RelatedCode;
        Related.Insert(false);
    end;
}
