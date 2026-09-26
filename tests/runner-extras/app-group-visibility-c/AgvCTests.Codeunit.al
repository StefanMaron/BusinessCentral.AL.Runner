// App group C declares a dependency on A, so A's table IS part of its object inventory and
// B's is not. The visible tests here are what separate 'own objects plus the declared
// dependency closure' from 'own objects only'.
// Issue #2279; the mechanism is docs/virtual-tables-allobj.md#app-group-visibility.
codeunit 62622 "AGV C Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "AGV C Assert";

    [Test]
    procedure AllObj_OwnTable_IsListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsTrue(AllObj.Get(AllObj."Object Type"::Table, 62620), 'AllObj in app group C must list its own table 62620');
        Assert.AreEqual('AGV C Table', AllObj."Object Name", 'AllObj row for table 62620');
    end;

    [Test]
    procedure AllObjWithCaption_OwnTable_IsListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsTrue(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62620), 'AllObjWithCaption in app group C must list its own table 62620');
        Assert.AreEqual('AGV C Table Caption', AllObjWithCaption."Object Caption", 'AllObjWithCaption row for table 62620');
    end;

    [Test]
    procedure TableMetadata_OwnTable_IsListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsTrue(TableMetadata.Get(62620), 'Table Metadata in app group C must list its own table 62620');
        Assert.AreEqual('AGV C Table', TableMetadata.Name, 'Table Metadata row for table 62620');
    end;

    [Test]
    procedure AllObj_DependencyATable_IsListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsTrue(AllObj.Get(AllObj."Object Type"::Table, 62600), 'AllObj in app group C must list the table of its dependency A, 62600');
        Assert.AreEqual('AGV A Table', AllObj."Object Name", 'AllObj row for table 62600');
    end;

    [Test]
    procedure AllObjWithCaption_DependencyATable_IsListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsTrue(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62600), 'AllObjWithCaption in app group C must list the table of its dependency A, 62600');
        Assert.AreEqual('AGV A Table Caption', AllObjWithCaption."Object Caption", 'AllObjWithCaption row for table 62600');
    end;

    [Test]
    procedure TableMetadata_DependencyATable_IsListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsTrue(TableMetadata.Get(62600), 'Table Metadata in app group C must list the table of its dependency A, 62600');
        Assert.AreEqual('AGV A Table', TableMetadata.Name, 'Table Metadata row for table 62600');
    end;

    [Test]
    procedure AllObj_GroupBTable_IsNotListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsFalse(AllObj.Get(AllObj."Object Type"::Table, 62610), 'AllObj in app group C lists table 62610 of unrelated app group B');
        AllObj.SetRange("Object ID", 62610, 62619);
        Assert.IsTrue(AllObj.IsEmpty(), 'AllObj in app group C lists an object in the id range of unrelated app group B');
    end;

    [Test]
    procedure AllObjWithCaption_GroupBTable_IsNotListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsFalse(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62610), 'AllObjWithCaption in app group C lists table 62610 of unrelated app group B');
        AllObjWithCaption.SetRange("Object ID", 62610, 62619);
        Assert.IsTrue(AllObjWithCaption.IsEmpty(), 'AllObjWithCaption in app group C lists an object in the id range of unrelated app group B');
    end;

    [Test]
    procedure TableMetadata_GroupBTable_IsNotListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsFalse(TableMetadata.Get(62610), 'Table Metadata in app group C lists table 62610 of unrelated app group B');
        TableMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(TableMetadata.IsEmpty(), 'Table Metadata in app group C lists a table in the id range of unrelated app group B');
    end;

    // #4070: the dependency-closure control on the two surfaces this issue adds. A filter
    // keyed on the executing app ALONE would hide A's rows here and pass every group-A
    // assertion, so these are what separate the closure from "own objects only".
    [Test]
    procedure FieldTable_OwnTable_IsListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsTrue(FieldRec.Get(62620, 1), 'Field in app group C must list field 1 of its own table 62620');
        Assert.AreEqual('Code', FieldRec.FieldName, 'Field row for table 62620 field 1');
    end;

    [Test]
    procedure FieldTable_DependencyATable_IsListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsTrue(FieldRec.Get(62600, 1), 'Field in app group C must list field 1 of the table of its dependency A, 62600');
        Assert.AreEqual('Code', FieldRec.FieldName, 'Field row for table 62600 field 1');
    end;

    [Test]
    procedure FieldTable_GroupBTable_IsNotListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsFalse(FieldRec.Get(62610, 1), 'Field in app group C lists field 1 of table 62610 of unrelated app group B');
        FieldRec.SetRange(TableNo, 62610, 62619);
        Assert.IsTrue(FieldRec.IsEmpty(), 'Field in app group C lists a field of a table in the id range of unrelated app group B');
    end;

    [Test]
    procedure CodeunitMetadata_OwnCodeunit_IsListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsTrue(CodeunitMetadata.Get(62622), 'CodeUnit Metadata in app group C must list its own codeunit 62622');
        Assert.AreEqual('AGV C Tests', CodeunitMetadata.Name, 'CodeUnit Metadata row for codeunit 62622');
    end;

    [Test]
    procedure CodeunitMetadata_DependencyACodeunit_IsListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsTrue(CodeunitMetadata.Get(62602), 'CodeUnit Metadata in app group C must list the codeunit of its dependency A, 62602');
        Assert.AreEqual('AGV A Tests', CodeunitMetadata.Name, 'CodeUnit Metadata row for codeunit 62602');
    end;

    [Test]
    procedure CodeunitMetadata_GroupBCodeunit_IsNotListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsFalse(CodeunitMetadata.Get(62612), 'CodeUnit Metadata in app group C lists codeunit 62612 of unrelated app group B');
        CodeunitMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(CodeunitMetadata.IsEmpty(), 'CodeUnit Metadata in app group C lists a codeunit in the id range of unrelated app group B');
    end;
    // #4447: the three metadata tables this issue adds. The dependency-closure controls are
    // the ones that matter here -- a filter keyed on the EXECUTING APP ALONE hides A's page
    // and report from C, passes every group-A and group-B assertion, and is still wrong.
    [Test]
    procedure PageMetadata_OwnPage_IsListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsTrue(PageMetadata.Get(62623), 'Page Metadata in app group C must list its own page 62623');
        Assert.AreEqual('AGV C Page', PageMetadata.Name, 'Page Metadata row for page 62623');
    end;

    [Test]
    procedure PageMetadata_DependencyAPage_IsListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsTrue(PageMetadata.Get(62603), 'Page Metadata in app group C must list the page of its dependency A, 62603');
        Assert.AreEqual('AGV A Page', PageMetadata.Name, 'Page Metadata row for page 62603');
    end;

    [Test]
    procedure PageMetadata_GroupBPage_IsNotListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsFalse(PageMetadata.Get(62613), 'Page Metadata in app group C lists page 62613 of unrelated app group B');
        PageMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(PageMetadata.IsEmpty(), 'Page Metadata in app group C lists a page in the id range of unrelated app group B');
    end;

    [Test]
    procedure ReportMetadata_OwnReport_IsListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsTrue(ReportMetadata.Get(62624), 'Report Metadata in app group C must list its own report 62624');
        Assert.AreEqual('AGV C Report', ReportMetadata.Name, 'Report Metadata row for report 62624');
    end;

    [Test]
    procedure ReportMetadata_DependencyAReport_IsListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsTrue(ReportMetadata.Get(62604), 'Report Metadata in app group C must list the report of its dependency A, 62604');
        Assert.AreEqual('AGV A Report', ReportMetadata.Name, 'Report Metadata row for report 62604');
    end;

    [Test]
    procedure ReportMetadata_GroupBReport_IsNotListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsFalse(ReportMetadata.Get(62614), 'Report Metadata in app group C lists report 62614 of unrelated app group B');
        ReportMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(ReportMetadata.IsEmpty(), 'Report Metadata in app group C lists a report in the id range of unrelated app group B');
    end;

    [Test]
    procedure ReportDataItems_OwnReport_IsListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62624);
        Assert.IsTrue(ReportDataItems.FindFirst(), 'Report Data Items in app group C must list the data item of its own report 62624');
        Assert.AreEqual('AgvCRows', ReportDataItems.Name, 'Report Data Items row for report 62624');
        Assert.AreEqual(62620, ReportDataItems."Related Table ID", 'Report Data Items related table for report 62624');
    end;

    [Test]
    procedure ReportDataItems_DependencyAReport_IsListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62604);
        Assert.IsTrue(ReportDataItems.FindFirst(), 'Report Data Items in app group C must list the data item of the report of its dependency A, 62604');
        Assert.AreEqual('AgvARows', ReportDataItems.Name, 'Report Data Items row for report 62604');
        Assert.AreEqual(62600, ReportDataItems."Related Table ID", 'Report Data Items related table for report 62604');
    end;

    [Test]
    procedure ReportDataItems_GroupBReport_IsNotListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62614);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group C lists the data item of report 62614 of unrelated app group B');
        ReportDataItems.SetRange("Report ID", 62610, 62619);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group C lists a data item of a report in the id range of unrelated app group B');
    end;

    // #4447: Query Metadata (2000000142). Served by BC's OWN QueryDataProvider walking the
    // object snapshot, so the filter sits in the snapshot builder, not a runner populate arm.
    // The dependency-closure arm below is the one that matters: a filter keyed on the
    // EXECUTING APP ALONE hides A's query here and still passes every group-A and group-B
    // assertion.
    [Test]
    procedure QueryMetadata_OwnQuery_IsListed()
    var
        QueryMetadata: Record "Query Metadata";
    begin
        Assert.IsTrue(QueryMetadata.Get(62625), 'Query Metadata in app group C must list its own query 62625');
        Assert.AreEqual('AGV C Query', QueryMetadata.Name, 'Query Metadata row for query 62625');
    end;

    [Test]
    procedure QueryMetadata_DependencyAQuery_IsListed()
    var
        QueryMetadata: Record "Query Metadata";
    begin
        Assert.IsTrue(QueryMetadata.Get(62605), 'Query Metadata in app group C must list the query of its dependency A, 62605');
        Assert.AreEqual('AGV A Query', QueryMetadata.Name, 'Query Metadata row for query 62605');
    end;

    [Test]
    procedure QueryMetadata_GroupBQuery_IsNotListed()
    var
        QueryMetadata: Record "Query Metadata";
    begin
        Assert.IsFalse(QueryMetadata.Get(62615), 'Query Metadata in app group C lists query 62615 of unrelated app group B');
        QueryMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(QueryMetadata.IsEmpty(), 'Query Metadata in app group C lists a query in the id range of unrelated app group B');
    end;

    // #4461: XMLport Metadata (2000000280), served like Query Metadata by BC's OWN provider
    // (XmlPortDataProvider) walking the object snapshot, so the app-group filter sits in the
    // snapshot builder. The own-xmlport arm is the positive control for the hiding arms.
    [Test]
    procedure XmlPortMetadata_OwnXmlPort_IsListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsTrue(XmlPortMetadata.Get(62626), 'XMLport Metadata in app group C must list its own xmlport 62626');
        Assert.AreEqual('AGV C XmlPort', XmlPortMetadata.Name, 'XMLport Metadata row for xmlport 62626');
    end;

    [Test]
    procedure XmlPortMetadata_DependencyAXmlPort_IsListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsTrue(XmlPortMetadata.Get(62606), 'XMLport Metadata in app group C must list the xmlport of its dependency A, 62606');
        Assert.AreEqual('AGV A XmlPort', XmlPortMetadata.Name, 'XMLport Metadata row for xmlport 62606');
    end;

    [Test]
    procedure XmlPortMetadata_GroupBXmlPort_IsNotListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsFalse(XmlPortMetadata.Get(62616), 'XMLport Metadata in app group C lists xmlport 62616 of unrelated app group B');
        XmlPortMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(XmlPortMetadata.IsEmpty(), 'XMLport Metadata in app group C lists an xmlport in the id range of unrelated app group B');
    end;
}
