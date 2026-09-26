// App group A declares no dependencies, so its object inventory holds its own objects and
// nothing from the sibling groups B and C, whichever of them compiled or ran first.
// Issue #2279; the mechanism is docs/virtual-tables-allobj.md#app-group-visibility.
codeunit 62602 "AGV A Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "AGV A Assert";

    [Test]
    procedure AllObj_OwnTable_IsListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsTrue(AllObj.Get(AllObj."Object Type"::Table, 62600), 'AllObj in app group A must list its own table 62600');
        Assert.AreEqual('AGV A Table', AllObj."Object Name", 'AllObj row for table 62600');
    end;

    [Test]
    procedure AllObjWithCaption_OwnTable_IsListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsTrue(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62600), 'AllObjWithCaption in app group A must list its own table 62600');
        Assert.AreEqual('AGV A Table Caption', AllObjWithCaption."Object Caption", 'AllObjWithCaption row for table 62600');
    end;

    [Test]
    procedure TableMetadata_OwnTable_IsListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsTrue(TableMetadata.Get(62600), 'Table Metadata in app group A must list its own table 62600');
        Assert.AreEqual('AGV A Table', TableMetadata.Name, 'Table Metadata row for table 62600');
    end;

    [Test]
    procedure AllObj_GroupBTable_IsNotListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsFalse(AllObj.Get(AllObj."Object Type"::Table, 62610), 'AllObj in app group A lists table 62610 of unrelated app group B');
        AllObj.SetRange("Object ID", 62610, 62619);
        Assert.IsTrue(AllObj.IsEmpty(), 'AllObj in app group A lists an object in the id range of unrelated app group B');
    end;

    [Test]
    procedure AllObjWithCaption_GroupBTable_IsNotListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsFalse(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62610), 'AllObjWithCaption in app group A lists table 62610 of unrelated app group B');
        AllObjWithCaption.SetRange("Object ID", 62610, 62619);
        Assert.IsTrue(AllObjWithCaption.IsEmpty(), 'AllObjWithCaption in app group A lists an object in the id range of unrelated app group B');
    end;

    [Test]
    procedure TableMetadata_GroupBTable_IsNotListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsFalse(TableMetadata.Get(62610), 'Table Metadata in app group A lists table 62610 of unrelated app group B');
        TableMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(TableMetadata.IsEmpty(), 'Table Metadata in app group A lists a table in the id range of unrelated app group B');
    end;

    [Test]
    procedure AllObj_GroupCTable_IsNotListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsFalse(AllObj.Get(AllObj."Object Type"::Table, 62620), 'AllObj in app group A lists table 62620 of unrelated app group C');
        AllObj.SetRange("Object ID", 62620, 62629);
        Assert.IsTrue(AllObj.IsEmpty(), 'AllObj in app group A lists an object in the id range of unrelated app group C');
    end;

    [Test]
    procedure AllObjWithCaption_GroupCTable_IsNotListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsFalse(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62620), 'AllObjWithCaption in app group A lists table 62620 of unrelated app group C');
        AllObjWithCaption.SetRange("Object ID", 62620, 62629);
        Assert.IsTrue(AllObjWithCaption.IsEmpty(), 'AllObjWithCaption in app group A lists an object in the id range of unrelated app group C');
    end;

    [Test]
    procedure TableMetadata_GroupCTable_IsNotListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsFalse(TableMetadata.Get(62620), 'Table Metadata in app group A lists table 62620 of unrelated app group C');
        TableMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(TableMetadata.IsEmpty(), 'Table Metadata in app group A lists a table in the id range of unrelated app group C');
    end;

    // #4070: the same scoping, on the two other virtual tables fed from the process-wide
    // parsed-object registries. Field is keyed on the SOURCE table id, CodeUnit Metadata on
    // the codeunit id, so each names its own kind in the owner map.
    [Test]
    procedure FieldTable_OwnTable_IsListed()
    var
        FieldRec: Record "Field";
    begin
        FieldRec.SetRange(TableNo, 62600);
        Assert.IsFalse(FieldRec.IsEmpty(), 'Field in app group A must list the fields of its own table 62600');
        Assert.IsTrue(FieldRec.Get(62600, 1), 'Field in app group A must list field 1 of its own table 62600');
        Assert.AreEqual('Code', FieldRec.FieldName, 'Field row for table 62600 field 1');
    end;

    [Test]
    procedure FieldTable_GroupBTable_IsNotListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsFalse(FieldRec.Get(62610, 1), 'Field in app group A lists field 1 of table 62610 of unrelated app group B');
        FieldRec.SetRange(TableNo, 62610, 62619);
        Assert.IsTrue(FieldRec.IsEmpty(), 'Field in app group A lists a field of a table in the id range of unrelated app group B');
    end;

    [Test]
    procedure FieldTable_GroupCTable_IsNotListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsFalse(FieldRec.Get(62620, 1), 'Field in app group A lists field 1 of table 62620 of unrelated app group C');
        FieldRec.SetRange(TableNo, 62620, 62629);
        Assert.IsTrue(FieldRec.IsEmpty(), 'Field in app group A lists a field of a table in the id range of unrelated app group C');
    end;

    [Test]
    procedure CodeunitMetadata_OwnCodeunit_IsListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsTrue(CodeunitMetadata.Get(62602), 'CodeUnit Metadata in app group A must list its own codeunit 62602');
        Assert.AreEqual('AGV A Tests', CodeunitMetadata.Name, 'CodeUnit Metadata row for codeunit 62602');
    end;

    [Test]
    procedure CodeunitMetadata_GroupBCodeunit_IsNotListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsFalse(CodeunitMetadata.Get(62612), 'CodeUnit Metadata in app group A lists codeunit 62612 of unrelated app group B');
        CodeunitMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(CodeunitMetadata.IsEmpty(), 'CodeUnit Metadata in app group A lists a codeunit in the id range of unrelated app group B');
    end;

    [Test]
    procedure CodeunitMetadata_GroupCCodeunit_IsNotListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsFalse(CodeunitMetadata.Get(62622), 'CodeUnit Metadata in app group A lists codeunit 62622 of unrelated app group C');
        CodeunitMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(CodeunitMetadata.IsEmpty(), 'CodeUnit Metadata in app group A lists a codeunit in the id range of unrelated app group C');
    end;

    // #4447: the three metadata tables this issue adds. The page and report these assert on
    // were added with these tests -- the earlier probe for this claim asserted the NEGATIVE
    // direction in a run where no fixture group declared a page at all, so it was green and
    // about nothing. These positive controls are what make the negatives below mean something.
    [Test]
    procedure PageMetadata_OwnPage_IsListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsTrue(PageMetadata.Get(62603), 'Page Metadata in app group A must list its own page 62603');
        Assert.AreEqual('AGV A Page', PageMetadata.Name, 'Page Metadata row for page 62603');
    end;

    [Test]
    procedure ReportMetadata_OwnReport_IsListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsTrue(ReportMetadata.Get(62604), 'Report Metadata in app group A must list its own report 62604');
        Assert.AreEqual('AGV A Report', ReportMetadata.Name, 'Report Metadata row for report 62604');
    end;

    [Test]
    procedure ReportDataItems_OwnReport_IsListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62604);
        Assert.IsTrue(ReportDataItems.FindFirst(), 'Report Data Items in app group A must list the data item of its own report 62604');
        Assert.AreEqual('AgvARows', ReportDataItems.Name, 'Report Data Items row for report 62604');
        Assert.AreEqual(62600, ReportDataItems."Related Table ID", 'Report Data Items related table for report 62604');
    end;

    [Test]
    procedure PageMetadata_GroupBPage_IsNotListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsFalse(PageMetadata.Get(62613), 'Page Metadata in app group A lists page 62613 of unrelated app group B');
        PageMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(PageMetadata.IsEmpty(), 'Page Metadata in app group A lists a page in the id range of unrelated app group B');
    end;

    [Test]
    procedure ReportMetadata_GroupBReport_IsNotListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsFalse(ReportMetadata.Get(62614), 'Report Metadata in app group A lists report 62614 of unrelated app group B');
        ReportMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(ReportMetadata.IsEmpty(), 'Report Metadata in app group A lists a report in the id range of unrelated app group B');
    end;

    [Test]
    procedure ReportDataItems_GroupBReport_IsNotListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62614);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group A lists the data item of report 62614 of unrelated app group B');
        ReportDataItems.SetRange("Report ID", 62610, 62619);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group A lists a data item of a report in the id range of unrelated app group B');
    end;

    [Test]
    procedure PageMetadata_GroupCPage_IsNotListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsFalse(PageMetadata.Get(62623), 'Page Metadata in app group A lists page 62623 of unrelated app group C');
        PageMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(PageMetadata.IsEmpty(), 'Page Metadata in app group A lists a page in the id range of unrelated app group C');
    end;

    [Test]
    procedure ReportMetadata_GroupCReport_IsNotListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsFalse(ReportMetadata.Get(62624), 'Report Metadata in app group A lists report 62624 of unrelated app group C');
        ReportMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(ReportMetadata.IsEmpty(), 'Report Metadata in app group A lists a report in the id range of unrelated app group C');
    end;

    [Test]
    procedure ReportDataItems_GroupCReport_IsNotListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62624);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group A lists the data item of report 62624 of unrelated app group C');
        ReportDataItems.SetRange("Report ID", 62620, 62629);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group A lists a data item of a report in the id range of unrelated app group C');
    end;

    // #4447: Query Metadata (2000000142). Unlike the three tables above it is served by BC's
    // OWN QueryDataProvider, which walks the object snapshot -- so the filter sits in the
    // snapshot builder rather than in a runner populate arm. This is the positive control that
    // stops the hiding assertions below being vacuous: the query must exist and be described.
    [Test]
    procedure QueryMetadata_OwnQuery_IsListed()
    var
        QueryMetadata: Record "Query Metadata";
    begin
        Assert.IsTrue(QueryMetadata.Get(62605), 'Query Metadata in app group A must list its own query 62605');
        Assert.AreEqual('AGV A Query', QueryMetadata.Name, 'Query Metadata row for query 62605');
    end;

    [Test]
    procedure QueryMetadata_GroupBQuery_IsNotListed()
    var
        QueryMetadata: Record "Query Metadata";
    begin
        Assert.IsFalse(QueryMetadata.Get(62615), 'Query Metadata in app group A lists query 62615 of unrelated app group B');
        QueryMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(QueryMetadata.IsEmpty(), 'Query Metadata in app group A lists a query in the id range of unrelated app group B');
    end;

    [Test]
    procedure QueryMetadata_GroupCQuery_IsNotListed()
    var
        QueryMetadata: Record "Query Metadata";
    begin
        Assert.IsFalse(QueryMetadata.Get(62625), 'Query Metadata in app group A lists query 62625 of unrelated app group C');
        QueryMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(QueryMetadata.IsEmpty(), 'Query Metadata in app group A lists a query in the id range of unrelated app group C');
    end;

    // #4461: XMLport Metadata (2000000280), served like Query Metadata by BC's OWN provider
    // (XmlPortDataProvider) walking the object snapshot, so the app-group filter sits in the
    // snapshot builder. The own-xmlport arm is the positive control for the hiding arms.
    [Test]
    procedure XmlPortMetadata_OwnXmlPort_IsListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsTrue(XmlPortMetadata.Get(62606), 'XMLport Metadata in app group A must list its own xmlport 62606');
        Assert.AreEqual('AGV A XmlPort', XmlPortMetadata.Name, 'XMLport Metadata row for xmlport 62606');
    end;

    [Test]
    procedure XmlPortMetadata_GroupBXmlPort_IsNotListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsFalse(XmlPortMetadata.Get(62616), 'XMLport Metadata in app group A lists xmlport 62616 of unrelated app group B');
        XmlPortMetadata.SetRange(ID, 62610, 62619);
        Assert.IsTrue(XmlPortMetadata.IsEmpty(), 'XMLport Metadata in app group A lists an xmlport in the id range of unrelated app group B');
    end;

    [Test]
    procedure XmlPortMetadata_GroupCXmlPort_IsNotListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsFalse(XmlPortMetadata.Get(62626), 'XMLport Metadata in app group A lists xmlport 62626 of unrelated app group C');
        XmlPortMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(XmlPortMetadata.IsEmpty(), 'XMLport Metadata in app group A lists an xmlport in the id range of unrelated app group C');
    end;
}
