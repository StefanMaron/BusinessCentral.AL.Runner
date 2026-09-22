// Issue #4452 bundle 2 of 2 — the bundle the measurement is about. Runs SECOND.
//
// By the time this runs, bundle 2's source dir HAS been registered, so every object below is
// bundle 2's own and fully parsed. If _metaFormCache / _metaReportCache / _metaQueryCache /
// _metaXmlPortCache have _metaTableCache's cached-null shape AND no fallback absorbs it, these
// assertions fail — bundle 1 already asked about each id before it was parsed.
codeunit 66222 "Rnce Second Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Rnce Second Assert";

    [Test]
    procedure OwnPage_PageMetadata_AnswersRealName()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsTrue(PageMetadata.Get(66223), 'Page Metadata in bundle 2 must list its own page 66223');
        Assert.AreEqual('Rnce Second Page', PageMetadata.Name, 'Page Metadata Name for page 66223');
    end;

    [Test]
    procedure OwnReport_ReportMetadata_AnswersRealName()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsTrue(ReportMetadata.Get(66224), 'Report Metadata in bundle 2 must list its own report 66224');
        Assert.AreEqual('Rnce Second Report', ReportMetadata.Name, 'Report Metadata Name for report 66224');
    end;

    [Test]
    procedure OwnQuery_AllObj_ListsIt()
    var
        AllObjRec: Record AllObj;
    begin
        Assert.IsTrue(AllObjRec.Get(AllObjRec."Object Type"::Query, 66225), 'AllObj in bundle 2 must list its own query 66225');
        Assert.AreEqual('Rnce Second Query', AllObjRec."Object Name", 'AllObj Object Name for query 66225');
    end;

    [Test]
    procedure OwnXmlPort_AllObj_ListsIt()
    var
        AllObjRec: Record AllObj;
    begin
        Assert.IsTrue(AllObjRec.Get(AllObjRec."Object Type"::XMLport, 66226), 'AllObj in bundle 2 must list its own xmlport 66226');
        Assert.AreEqual('Rnce Second XmlPort', AllObjRec."Object Name", 'AllObj Object Name for xmlport 66226');
    end;

    // The RUN path, which is the one a cached null would actually break: running the report and
    // the xmlport reaches NCLMetadata.GetMetaApplicationObject for real, rather than a virtual
    // table that may source its rows from somewhere else entirely.
    [Test]
    procedure OwnReport_CanBeRun()
    var
        Rep: Report "Rnce Second Report";
    begin
        Rep.UseRequestPage(false);
        Rep.Run();
    end;

    [Test]
    procedure OwnQuery_CanBeOpened()
    var
        Q: Query "Rnce Second Query";
    begin
        // Open() answers true once the query is materialised; Read() then answers false because
        // the table is empty. Asserting Open()=true is the claim -- it is the call that needs
        // the query's metadata, which is what a poisoned _metaQueryCache would deny.
        Assert.IsTrue(Q.Open(), 'Query 66225 must open: its metadata is bundle 2''s own');
        Assert.IsFalse(Q.Read(), 'Query 66225 has no rows, so the first Read must answer false');
        Q.Close();
    end;

    // The negative direction, so a fix cannot be "rebuild everything for everyone". Bundle 1's
    // page 66203 was NEVER the subject of a poisoned lookup, so whatever its cross-group
    // visibility is (that is #4070's question, not this one), asking about it here must not
    // throw the "no loadable page metadata" refusal that a poisoned cache produces.
    [Test]
    procedure AskingAboutTheFirstBundlesPage_DoesNotRefuse()
    var
        PageMetadata: Record "Page Metadata";
    begin
        if PageMetadata.Get(66203) then;
    end;
}
