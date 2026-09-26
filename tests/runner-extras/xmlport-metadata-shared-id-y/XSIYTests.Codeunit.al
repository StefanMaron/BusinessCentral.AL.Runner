// #4461: an xmlport id two unrelated app groups both declare has no single source owner, so
// neither group's XMLport Metadata may hide it. Group Y is the discriminating one: it runs after
// group X's assembly is loaded, which is what the compiled-assembly owner evidence reads.
// The row's Name is not asserted: the later group reads the earlier group's metadata (#4751).
codeunit 66311 "XSIY Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "XSIY Assert";

    [Test]
    procedure XmlPortMetadata_SharedIdXmlPort_IsListed()
    var
        XmlPortMetadata: Record "XmlPort Metadata";
    begin
        Assert.IsTrue(XmlPortMetadata.Get(66300), 'XMLport Metadata in group Y must list xmlport 66300, which this group declares');
    end;
}
