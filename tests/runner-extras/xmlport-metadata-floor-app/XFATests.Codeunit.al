// #4461: this app states only the application floor, so System Application is NOT in its
// explicit dependency list. XMLport Metadata must still list System Application's xmlports.
codeunit 66320 "XFA Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "XFA Assert";

    [Test]
    procedure XmlPortMetadata_SystemApplicationXmlPort_IsListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsTrue(XmlPortMetadata.Get(9862), 'XMLport Metadata must list System Application xmlport 9862 for an app stating only the application floor');
        Assert.AreEqual('Export Permission Sets System', XmlPortMetadata.Name, 'XMLport Metadata row for xmlport 9862');
    end;

    [Test]
    procedure XmlPortMetadata_FloorApp_ListsPrecompiledXmlPorts()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsTrue(XmlPortMetadata.Count() > 0, 'XMLport Metadata must not be empty for an app stating only the application floor');
    end;
}
