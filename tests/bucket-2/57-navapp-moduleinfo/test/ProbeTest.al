codeunit 56571 "NA Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetModuleInfoDoesNotCrash()
    var
        Probe: Codeunit "NA Probe";
    begin
        // [GIVEN] An unknown GUID passed to NavApp.GetModuleInfo
        // [THEN] Must not throw; returning "<unknown>" is the test contract
        Assert.AreEqual('<unknown>', Probe.TryUnknown(), 'Unknown GUID must return <unknown>');
    end;

    [Test]
    procedure DefaultModuleInfoNameIsEmpty()
    var
        Probe: Codeunit "NA Probe";
    begin
        // A default ModuleInfo instance must have a readable Name (empty by default)
        Assert.AreEqual('', Probe.ReadsNamePropertyWhenMissing(), 'Default ModuleInfo.Name is empty string');
    end;
}
