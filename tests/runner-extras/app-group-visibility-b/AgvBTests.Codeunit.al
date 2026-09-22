// App group B declares no dependencies, so its object inventory holds its own objects and
// nothing from the sibling groups A and C. B runs after A, which is the direction #2279 was
// first measured in.
// Issue #2279; the mechanism is docs/virtual-tables-allobj.md#app-group-visibility.
codeunit 62612 "AGV B Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "AGV B Assert";

    [Test]
    procedure AllObj_OwnTable_IsListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsTrue(AllObj.Get(AllObj."Object Type"::Table, 62610), 'AllObj in app group B must list its own table 62610');
        Assert.AreEqual('AGV B Table', AllObj."Object Name", 'AllObj row for table 62610');
    end;

    [Test]
    procedure AllObjWithCaption_OwnTable_IsListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsTrue(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62610), 'AllObjWithCaption in app group B must list its own table 62610');
        Assert.AreEqual('AGV B Table Caption', AllObjWithCaption."Object Caption", 'AllObjWithCaption row for table 62610');
    end;

    [Test]
    procedure TableMetadata_OwnTable_IsListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsTrue(TableMetadata.Get(62610), 'Table Metadata in app group B must list its own table 62610');
        Assert.AreEqual('AGV B Table', TableMetadata.Name, 'Table Metadata row for table 62610');
    end;

    [Test]
    procedure AllObj_GroupATable_IsNotListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsFalse(AllObj.Get(AllObj."Object Type"::Table, 62600), 'AllObj in app group B lists table 62600 of unrelated app group A');
        AllObj.SetRange("Object ID", 62600, 62609);
        Assert.IsTrue(AllObj.IsEmpty(), 'AllObj in app group B lists an object in the id range of unrelated app group A');
    end;

    [Test]
    procedure AllObjWithCaption_GroupATable_IsNotListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsFalse(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62600), 'AllObjWithCaption in app group B lists table 62600 of unrelated app group A');
        AllObjWithCaption.SetRange("Object ID", 62600, 62609);
        Assert.IsTrue(AllObjWithCaption.IsEmpty(), 'AllObjWithCaption in app group B lists an object in the id range of unrelated app group A');
    end;

    [Test]
    procedure TableMetadata_GroupATable_IsNotListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsFalse(TableMetadata.Get(62600), 'Table Metadata in app group B lists table 62600 of unrelated app group A');
        TableMetadata.SetRange(ID, 62600, 62609);
        Assert.IsTrue(TableMetadata.IsEmpty(), 'Table Metadata in app group B lists a table in the id range of unrelated app group A');
    end;

    [Test]
    procedure AllObj_GroupCTable_IsNotListed()
    var
        AllObj: Record AllObj;
    begin
        Assert.IsFalse(AllObj.Get(AllObj."Object Type"::Table, 62620), 'AllObj in app group B lists table 62620 of unrelated app group C');
        AllObj.SetRange("Object ID", 62620, 62629);
        Assert.IsTrue(AllObj.IsEmpty(), 'AllObj in app group B lists an object in the id range of unrelated app group C');
    end;

    [Test]
    procedure AllObjWithCaption_GroupCTable_IsNotListed()
    var
        AllObjWithCaption: Record AllObjWithCaption;
    begin
        Assert.IsFalse(AllObjWithCaption.Get(AllObjWithCaption."Object Type"::Table, 62620), 'AllObjWithCaption in app group B lists table 62620 of unrelated app group C');
        AllObjWithCaption.SetRange("Object ID", 62620, 62629);
        Assert.IsTrue(AllObjWithCaption.IsEmpty(), 'AllObjWithCaption in app group B lists an object in the id range of unrelated app group C');
    end;

    [Test]
    procedure TableMetadata_GroupCTable_IsNotListed()
    var
        TableMetadata: Record "Table Metadata";
    begin
        Assert.IsFalse(TableMetadata.Get(62620), 'Table Metadata in app group B lists table 62620 of unrelated app group C');
        TableMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(TableMetadata.IsEmpty(), 'Table Metadata in app group B lists a table in the id range of unrelated app group C');
    end;

    // #4070: the same two surfaces from the other no-dependency group, so a fix that happened
    // to work only for the group that ran first is visible. B declares no dependencies, so
    // neither A nor C may contribute a Field row or a CodeUnit Metadata row here.
    [Test]
    procedure FieldTable_OwnTable_IsListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsTrue(FieldRec.Get(62610, 1), 'Field in app group B must list field 1 of its own table 62610');
        Assert.AreEqual('Code', FieldRec.FieldName, 'Field row for table 62610 field 1');
    end;

    [Test]
    procedure FieldTable_GroupATable_IsNotListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsFalse(FieldRec.Get(62600, 1), 'Field in app group B lists field 1 of table 62600 of unrelated app group A');
        FieldRec.SetRange(TableNo, 62600, 62609);
        Assert.IsTrue(FieldRec.IsEmpty(), 'Field in app group B lists a field of a table in the id range of unrelated app group A');
    end;

    [Test]
    procedure FieldTable_GroupCTable_IsNotListed()
    var
        FieldRec: Record "Field";
    begin
        Assert.IsFalse(FieldRec.Get(62620, 1), 'Field in app group B lists field 1 of table 62620 of unrelated app group C');
        FieldRec.SetRange(TableNo, 62620, 62629);
        Assert.IsTrue(FieldRec.IsEmpty(), 'Field in app group B lists a field of a table in the id range of unrelated app group C');
    end;

    [Test]
    procedure CodeunitMetadata_OwnCodeunit_IsListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsTrue(CodeunitMetadata.Get(62612), 'CodeUnit Metadata in app group B must list its own codeunit 62612');
        Assert.AreEqual('AGV B Tests', CodeunitMetadata.Name, 'CodeUnit Metadata row for codeunit 62612');
    end;

    [Test]
    procedure CodeunitMetadata_GroupACodeunit_IsNotListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsFalse(CodeunitMetadata.Get(62602), 'CodeUnit Metadata in app group B lists codeunit 62602 of unrelated app group A');
        CodeunitMetadata.SetRange(ID, 62600, 62609);
        Assert.IsTrue(CodeunitMetadata.IsEmpty(), 'CodeUnit Metadata in app group B lists a codeunit in the id range of unrelated app group A');
    end;

    [Test]
    procedure CodeunitMetadata_GroupCCodeunit_IsNotListed()
    var
        CodeunitMetadata: Record "CodeUnit Metadata";
    begin
        Assert.IsFalse(CodeunitMetadata.Get(62622), 'CodeUnit Metadata in app group B lists codeunit 62622 of unrelated app group C');
        CodeunitMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(CodeunitMetadata.IsEmpty(), 'CodeUnit Metadata in app group B lists a codeunit in the id range of unrelated app group C');
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
        Assert.IsTrue(PageMetadata.Get(62613), 'Page Metadata in app group B must list its own page 62613');
        Assert.AreEqual('AGV B Page', PageMetadata.Name, 'Page Metadata row for page 62613');
    end;

    [Test]
    procedure ReportMetadata_OwnReport_IsListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsTrue(ReportMetadata.Get(62614), 'Report Metadata in app group B must list its own report 62614');
        Assert.AreEqual('AGV B Report', ReportMetadata.Name, 'Report Metadata row for report 62614');
    end;

    [Test]
    procedure ReportDataItems_OwnReport_IsListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62614);
        Assert.IsTrue(ReportDataItems.FindFirst(), 'Report Data Items in app group B must list the data item of its own report 62614');
        Assert.AreEqual('AgvBRows', ReportDataItems.Name, 'Report Data Items row for report 62614');
        Assert.AreEqual(62610, ReportDataItems."Related Table ID", 'Report Data Items related table for report 62614');
    end;

    [Test]
    procedure PageMetadata_GroupAPage_IsNotListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsFalse(PageMetadata.Get(62603), 'Page Metadata in app group B lists page 62603 of unrelated app group A');
        PageMetadata.SetRange(ID, 62600, 62609);
        Assert.IsTrue(PageMetadata.IsEmpty(), 'Page Metadata in app group B lists a page in the id range of unrelated app group A');
    end;

    [Test]
    procedure ReportMetadata_GroupAReport_IsNotListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsFalse(ReportMetadata.Get(62604), 'Report Metadata in app group B lists report 62604 of unrelated app group A');
        ReportMetadata.SetRange(ID, 62600, 62609);
        Assert.IsTrue(ReportMetadata.IsEmpty(), 'Report Metadata in app group B lists a report in the id range of unrelated app group A');
    end;

    [Test]
    procedure ReportDataItems_GroupAReport_IsNotListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62604);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group B lists the data item of report 62604 of unrelated app group A');
        ReportDataItems.SetRange("Report ID", 62600, 62609);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group B lists a data item of a report in the id range of unrelated app group A');
    end;

    [Test]
    procedure PageMetadata_GroupCPage_IsNotListed()
    var
        PageMetadata: Record "Page Metadata";
    begin
        Assert.IsFalse(PageMetadata.Get(62623), 'Page Metadata in app group B lists page 62623 of unrelated app group C');
        PageMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(PageMetadata.IsEmpty(), 'Page Metadata in app group B lists a page in the id range of unrelated app group C');
    end;

    [Test]
    procedure ReportMetadata_GroupCReport_IsNotListed()
    var
        ReportMetadata: Record "Report Metadata";
    begin
        Assert.IsFalse(ReportMetadata.Get(62624), 'Report Metadata in app group B lists report 62624 of unrelated app group C');
        ReportMetadata.SetRange(ID, 62620, 62629);
        Assert.IsTrue(ReportMetadata.IsEmpty(), 'Report Metadata in app group B lists a report in the id range of unrelated app group C');
    end;

    [Test]
    procedure ReportDataItems_GroupCReport_IsNotListed()
    var
        ReportDataItems: Record "Report Data Items";
    begin
        ReportDataItems.SetRange("Report ID", 62624);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group B lists the data item of report 62624 of unrelated app group C');
        ReportDataItems.SetRange("Report ID", 62620, 62629);
        Assert.IsTrue(ReportDataItems.IsEmpty(), 'Report Data Items in app group B lists a data item of a report in the id range of unrelated app group C');
    end;
}
