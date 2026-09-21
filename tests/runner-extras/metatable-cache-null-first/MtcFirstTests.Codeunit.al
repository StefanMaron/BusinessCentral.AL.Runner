// Bundle 1 of #4450's pair, and it runs FIRST.
//
// Its job is to perform the lookup that poisons the cache: asking any metadata consumer about
// table 66110 -- bundle 2's table -- while bundle 2's source dir has not been registered yet.
// BuildNCLMetaTable then returns null (the table really is unparsed at that instant) and
// _metaTableCache.GetOrAdd CACHES that null. The assertions here are the ones that were true on
// main and must STAY true after the fix: at THIS point 66110 genuinely is not bundle 1's to see.
//
// Issue #4450; the mechanism is docs/virtual-tables-allobj.md#multi-bundle-metatable-cache.
codeunit 66102 "MTC First Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "MTC First Assert";

    // THE POISONING LOOKUP. This is the test that makes the sibling bundle's assertions mean
    // something: it asks about table 66110 -- bundle 2's table -- during bundle 1's phase, which
    // on main is the lookup whose cached null is never evicted.
    //
    // A deliberate no-op test in the sense tdd.md names: the WHOLE claim is "asking does not
    // throw", so the name says _DoesNotThrow. What it asserts about 66110's visibility is
    // nothing, on purpose. "66110 is invisible here" is #4070's claim -- the Field table is not
    // yet scoped to the executing app group -- and asserting it would make this suite red for a
    // defect it is not about, in one argument order only. The VALUE of this test is the ask
    // itself, which is the precondition every assertion in the sibling bundle depends on; it is
    // written as a test rather than left implicit so that deleting it makes the dependency
    // visible rather than silently defusing the sibling suite.
    [Test]
    procedure AskingAboutTheSecondBundlesTable_DoesNotThrow()
    var
        TableMetadata: Record "Table Metadata";
        FieldRec: Record "Field";
    begin
        if TableMetadata.Get(66110) then;
        if FieldRec.Get(66110, 1) then;
    end;

    // Bundle 1's own table is described here. This is the CONTROL for the sibling bundle's
    // assertion: the first bundle's table was never the subject of a poisoned lookup, so it is
    // correct on main and must stay correct.
    [Test]
    procedure OwnTable_IsFullyDescribed()
    var
        TableMetadata: Record "Table Metadata";
        FieldRec: Record "Field";
    begin
        Assert.IsTrue(TableMetadata.Get(66100), 'Table Metadata in bundle 1 must list its own table 66100');
        Assert.AreEqual('MTC First Table', TableMetadata.Name, 'Table Metadata Name for table 66100');

        Assert.IsTrue(FieldRec.Get(66100, 1), 'Field in bundle 1 must list field 1 of its own table 66100');
        Assert.AreEqual('Code', FieldRec.FieldName, 'Field 1 name for table 66100');
        Assert.IsTrue(FieldRec.Get(66100, 2), 'Field in bundle 1 must list field 2 of its own table 66100');
        Assert.AreEqual('Description', FieldRec.FieldName, 'Field 2 name for table 66100');
    end;
}
