// #4448: this group is the only one declaring "AGVI Precompiled Install Dep", so its objects are
// listed here. The dependency's install trigger fires under every app group of the run and errors
// unless it can see its own table; the row it inserts is what proves it ran to completion.
// The mechanism is docs/virtual-tables-allobj.md#precompiled-package-visibility.
codeunit 66351 "AGVI Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "AGVI Assert";

    [Test]
    procedure DeclaredPrecompiledDep_TableIsListed_AndItsInstallTriggerCompleted()
    var
        AllObj: Record AllObj;
        InstallLog: Record "AGVI Install Log";
    begin
        Assert.IsTrue(AllObj.Get(AllObj."Object Type"::Table, 66360), 'AllObj must list table 66360 of the precompiled dependency this app declares');
        Assert.IsTrue(InstallLog.Get('INSTALLED'), 'the dependency install trigger must have run to completion and inserted its row');
    end;
}
