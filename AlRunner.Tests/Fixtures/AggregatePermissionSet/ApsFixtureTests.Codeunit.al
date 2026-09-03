codeunit 60702 "APS Fixture Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "APS Assert";

    [Test]
    procedure AggregatePermissionSet_ThisBundlesDeclaredPermissionSet_IsFound()
    // CLAIM (runner mechanism, issue #2357): a permission set declared by the CURRENT
    // bundle's own AL source -- not shipped in a precompiled .app -- is a row of
    // "Aggregate Permission Set". Before the fix this table was never populated at all;
    // Get() always raised "does not exist" regardless of what declared the role.
    var
        AggregatePermissionSet: Record "Aggregate Permission Set";
        ThisModule: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(ThisModule);

        Assert.IsTrue(
            AggregatePermissionSet.Get(
                AggregatePermissionSet.Scope::System, ThisModule.Id(), 'APS Fixture Perm Set'),
            'Get() must find a permission set this bundle declares from source');
        Assert.AreEqual(
            'APS Fixture Permission Set', AggregatePermissionSet.Name,
            'Name must be the permission set''s declared Caption');
    end;

    [Test]
    procedure AggregatePermissionSet_GetOnUndeclaredRoleId_Fails()
    // CLAIM: Get() on a role id nothing declares does not silently succeed.
    var
        AggregatePermissionSet: Record "Aggregate Permission Set";
        ThisModule: ModuleInfo;
    begin
        NavApp.GetCurrentModuleInfo(ThisModule);
        Assert.IsFalse(
            AggregatePermissionSet.Get(
                AggregatePermissionSet.Scope::System, ThisModule.Id(), 'NO SUCH PERM SET'),
            'Get() must return false for a role id nothing declares');
    end;
}
