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
}
