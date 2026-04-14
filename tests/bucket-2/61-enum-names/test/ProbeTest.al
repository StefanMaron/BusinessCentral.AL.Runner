codeunit 56611 "EN Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure EnumNamesReturnsThree()
    var
        Probe: Codeunit "EN Probe";
    begin
        Assert.AreEqual(3, Probe.NamesCount(), 'EN Stage has three declared members');
    end;

    [Test]
    procedure EnumNamesFirstIsDraft()
    var
        Probe: Codeunit "EN Probe";
    begin
        Assert.AreEqual('Draft', Probe.NamesFirst(), 'First declared member is Draft');
    end;
}
