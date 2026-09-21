// Bundle 2 of #4450's pair, and it runs SECOND. This is the codeunit the issue is about.
//
// By the time this runs, bundle 2's source dir HAS been registered and table 66110 IS in
// _parsedTables -- so every assertion below is about bundle 2's own, fully-parsed table. On main
// they fail anyway, because bundle 1 already asked about 66110 before it was parsed and
// _metaTableCache.GetOrAdd cached the null BuildNCLMetaTable correctly returned at that instant.
// GetOrAdd never replaces an existing entry, so nothing rebuilds it: the table answers null for
// the rest of the process and PopulateFieldVirtualTable skips it, silently.
//
// Positional, not identity-based: reverse the two paths on the command line and the failure moves
// to the other bundle. Each bundle ALONE passes, which is why the single-bundle consolidated
// tests/runner-extras run cannot observe this (issue #4450's comment thread).
//
// Issue #4450; the mechanism is docs/virtual-tables-allobj.md#multi-bundle-metatable-cache.
codeunit 66112 "MTC Second Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "MTC Second Assert";

    // The Get path -- DataAccess.InternalTryGetByPrimaryKeyAsync. This is the exact assertion
    // shape #4450 reported: Field.Get on the bundle's OWN table answering false.
    [Test]
    procedure OwnTable_FieldGet_AnswersRealFieldNames()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsTrue(FieldRec.Get(66110, 1), 'Field in bundle 2 must list field 1 of its own table 66110');
        Assert.AreEqual('Code', FieldRec.FieldName, 'Field 1 name for table 66110');
        Assert.IsTrue(FieldRec.Get(66110, 2), 'Field in bundle 2 must list field 2 of its own table 66110');
        Assert.AreEqual('Description', FieldRec.FieldName, 'Field 2 name for table 66110');
    end;

    // The find path -- SetRange/FindSet, a different DataAccess entry point than Get above, and
    // one the issue believed "recovers". It does not: both reach the same
    // EnsureTableInMetadataCache, so both see the cached null. Asserting a COUNT rather than
    // IsEmpty, because an assertion of absence would pass vacuously against the empty store the
    // defect produces -- which is the hazard the fix exists to remove.
    //
    // The filter is No. 1..2 rather than the whole table, because BC's Field table also lists
    // the six system fields (SystemId, SystemCreatedAt/By, SystemModifiedAt/By, and the row
    // version) -- measured as 8 rows total for this two-field table, single bundle and multi
    // bundle alike. Counting all 8 would pin a platform detail this test is not about; counting
    // the two declared ids pins exactly the claim.
    [Test]
    procedure OwnTable_FieldFindSet_EnumeratesEveryDeclaredField()
    var
        FieldRec: Record "Field";
        Seen: Integer;
    begin
        FieldRec.SetRange(TableNo, 66110);
        FieldRec.SetRange("No.", 1, 2);
        if FieldRec.FindSet() then
            repeat
                Seen += 1;
            until FieldRec.Next() = 0;
        Assert.AreEqual(2, Seen, 'Field in bundle 2 must enumerate both declared fields of its own table 66110');
    end;

    // Table Metadata over the same table: the cached null is one entry read by many consumers,
    // so the claim is not specific to the Field table.
    [Test]
    procedure OwnTable_TableMetadata_AnswersRealName()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsTrue(TableMetadata.Get(66110), 'Table Metadata in bundle 2 must list its own table 66110');
        Assert.AreEqual('MTC Second Table', TableMetadata.Name, 'Table Metadata Name for table 66110');
    end;

    // The negative direction, so the fix cannot be "make every table visible everywhere": bundle 1
    // declares no dependency on bundle 2 and vice versa, so bundle 1's table stays hidden here.
    [Test]
    procedure FirstBundleTable_IsStillNotVisibleHere()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsFalse(TableMetadata.Get(66100), 'Table Metadata in bundle 2 must not list table 66100 of the unrelated first bundle');
    end;
}
