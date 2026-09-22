// Issue #4452 bundle 1 of 2 — the POISONING bundle. Runs FIRST.
//
// Its job is to ask every metadata consumer about bundle 2's page/report/query/xmlport ids
// (66223/66224/66225/66226) while bundle 2's source dir has not been registered yet. If those
// caches have _metaTableCache's shape, the null each builder correctly returns at that instant
// is cached by GetOrAdd and never replaced.
codeunit 66202 "Rnce First Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Rnce First Assert";

    // THE POISONING LOOKUPS. Asserting nothing about visibility on purpose: whether bundle 2's
    // objects are visible here is #4070's question, not this one. The VALUE is the ask itself.
    [Test]
    procedure AskingAboutTheSecondBundlesObjects_DoesNotThrow()
    var
        PageMetadata: Record "Page Metadata";
        ReportMetadata: Record "Report Metadata";
        AllObjRec: Record AllObj;
    begin
        if PageMetadata.Get(66223) then;
        if ReportMetadata.Get(66224) then;

        // AllObj covers the query and xmlport kinds, which have no dedicated metadata table.
        if AllObjRec.Get(AllObjRec."Object Type"::Query, 66225) then;
        if AllObjRec.Get(AllObjRec."Object Type"::XMLport, 66226) then;

        // The metadata-table reads above do NOT reach NCLMetadata.GetMetaApplicationObject.
        // These do: the by-id RUN surfaces are the arbitrary-id consumers of the four caches,
        // the same role the Field virtual table plays for _metaTableCache in #4450. Each is
        // expected to fail here (bundle 2 is unparsed at this instant); the ask is the point.
        asserterror Report.Run(66224);
        asserterror Page.Run(66223);
    end;

    // The CONTROL: bundle 1's own objects were never the subject of a poisoned lookup, so they
    // are correct on main and must stay correct. Without this, a red in bundle 2 could not be
    // attributed to the poisoning rather than to the fixture never having worked.
    [Test]
    procedure OwnObjects_AreDescribedHere()
    var
        PageMetadata: Record "Page Metadata";
        ReportMetadata: Record "Report Metadata";
        AllObjRec: Record AllObj;
    begin
        Assert.IsTrue(PageMetadata.Get(66203), 'Page Metadata in bundle 1 must list its own page 66203');
        Assert.AreEqual('Rnce First Page', PageMetadata.Name, 'Page Metadata Name for page 66203');

        Assert.IsTrue(ReportMetadata.Get(66204), 'Report Metadata in bundle 1 must list its own report 66204');
        Assert.AreEqual('Rnce First Report', ReportMetadata.Name, 'Report Metadata Name for report 66204');

        Assert.IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Query, 66205), 'AllObj in bundle 1 must list its own query 66205');
        Assert.IsTrue(AllObjRec.Get(AllObjRec."Object Type"::XMLport, 66206), 'AllObj in bundle 1 must list its own xmlport 66206');
    end;
}
