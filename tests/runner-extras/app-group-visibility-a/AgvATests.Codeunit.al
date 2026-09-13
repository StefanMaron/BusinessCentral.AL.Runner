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
}
