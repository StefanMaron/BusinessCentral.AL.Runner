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

    [Test]
    procedure AggregatePermissionSet_TenantRowInsertedAfterEarlierTouch_IsVisible()
    // CLAIM (runner mechanism, issue #2473): PopulateAggregatePermissionSetVirtualTable
    // must NOT snapshot the Tenant Permission Set union at first touch. Before the fix it
    // gated on a one-shot ConditionalWeakTable flag per in-memory provider, so a SECOND
    // touch after AL inserted a brand new Tenant Permission Set row was a no-op and the
    // new row never appeared -- the root of a 14-test cascade in Microsoft's own
    // Tests-SINGLESERVER Codeunit134614 (see #2357/#2393).
    var
        AggregatePermissionSetFirstTouch: Record "Aggregate Permission Set";
        AggregatePermissionSet: Record "Aggregate Permission Set";
        AggregatePermissionSetAfterDelete: Record "Aggregate Permission Set";
        TenantPermissionSet: Record "Tenant Permission Set";
        EmptyGuid: Guid;
    begin
        // An EARLIER, unrelated touch of Aggregate Permission Set, through a DIFFERENT
        // record variable -- the same shape as the real cascade (#2357/#2393): one test
        // opens the table first, an unrelated later test then writes Tenant Permission Set
        // and expects the union to reflect it. A one-shot "populate once" implementation
        // freezes its answer at THIS moment. Role ID is Code[20] on this table, so the probe
        // value must fit. Get()'s return value is consumed (not used as a bare statement) so
        // a miss returns false instead of raising.
        if AggregatePermissionSetFirstTouch.Get(AggregatePermissionSetFirstTouch.Scope::System, EmptyGuid, 'NOT A REAL ROLE ID') then;

        if TenantPermissionSet.Get(EmptyGuid, 'APS NEW TENANT ROLE') then
            TenantPermissionSet.Delete();

        Clear(TenantPermissionSet);
        TenantPermissionSet."App ID" := EmptyGuid;
        TenantPermissionSet."Role ID" := 'APS NEW TENANT ROLE';
        TenantPermissionSet.Name := 'APS New Tenant Role';
        TenantPermissionSet.Assignable := true;
        TenantPermissionSet.Insert();

        Assert.IsTrue(
            AggregatePermissionSet.Get(AggregatePermissionSet.Scope::Tenant, EmptyGuid, 'APS NEW TENANT ROLE'),
            'a Tenant Permission Set row inserted AFTER an earlier touch of Aggregate Permission Set must be visible on a later touch');
        Assert.AreEqual(
            'APS New Tenant Role', AggregatePermissionSet.Name,
            'Name must round-trip from the newly-inserted Tenant Permission Set row');

        // Negative: deleting the row must not leave a ghost behind -- proves the fix fully
        // redrives the union rather than only ever topping it up. A THIRD, still-untouched
        // record variable, same reason as AggregatePermissionSetFirstTouch above: a NavRecord
        // resolves its DataAccess (and this file's populate call) once, on its own first
        // touch, and caches it thereafter -- reusing `AggregatePermissionSet` here would
        // prove nothing about a later touch.
        TenantPermissionSet.Delete();
        Assert.IsFalse(
            AggregatePermissionSetAfterDelete.Get(AggregatePermissionSetAfterDelete.Scope::Tenant, EmptyGuid, 'APS NEW TENANT ROLE'),
            'a Tenant Permission Set row deleted after being visible must not remain a ghost row in Aggregate Permission Set');
    end;

    [Test]
    procedure AggregatePermissionSet_SameRecordVariableReusedAcrossWrite_SeesFreshRow()
    // CLAIM (runner mechanism, issue #2504): the SAME record variable, reused for a
    // touch/insert/re-touch sequence -- exactly what TestPage "Permission Sets"' own row
    // walk does with ONE bound Rec across .First()/.Next() -- must see the fresh row too,
    // not just a freshly-declared variable's own first touch (#2473's fix alone only
    // covered that narrower case: a NavRecord resolves its DataAccess wrapper once, on its
    // own first Get()/Find(), and every LATER call on that SAME instance skipped this
    // file's populate step entirely before this fix). Real BC's own
    // VirtualAndTempTransactionalDataCache.TryFind/TryGetByPrimaryKey unconditionally
    // report a cache miss for every request, so a real service tier recomputes this table
    // on EVERY Get()/Find(), cached wrapper or not.
    var
        AggregatePermissionSet: Record "Aggregate Permission Set";
        TenantPermissionSet: Record "Tenant Permission Set";
        EmptyGuid: Guid;
    begin
        // First touch on this ONE variable -- primes it, same as any ordinary first Get().
        if AggregatePermissionSet.Get(AggregatePermissionSet.Scope::System, EmptyGuid, 'NOT A REAL ROLE ID') then;

        if TenantPermissionSet.Get(EmptyGuid, 'APS REUSED VAR ROLE') then
            TenantPermissionSet.Delete();

        Clear(TenantPermissionSet);
        TenantPermissionSet."App ID" := EmptyGuid;
        TenantPermissionSet."Role ID" := 'APS REUSED VAR ROLE';
        TenantPermissionSet.Name := 'APS Reused Var Role';
        TenantPermissionSet.Assignable := true;
        TenantPermissionSet.Insert();

        // SAME variable, second Get() -- this is exactly the shape that stayed stale before
        // the #2504 fix (only three separate variables proved #2473's own fix worked).
        Assert.IsTrue(
            AggregatePermissionSet.Get(AggregatePermissionSet.Scope::Tenant, EmptyGuid, 'APS REUSED VAR ROLE'),
            'a record variable reused for a second Get() after an intervening write must see the new row, not the state from its own first touch');
        Assert.AreEqual(
            'APS Reused Var Role', AggregatePermissionSet.Name,
            'Name must round-trip from the newly-inserted Tenant Permission Set row, read through the REUSED variable');

        TenantPermissionSet.Delete();
        // Negative, SAME variable again: a third Get() on it must not see a ghost either.
        Assert.IsFalse(
            AggregatePermissionSet.Get(AggregatePermissionSet.Scope::Tenant, EmptyGuid, 'APS REUSED VAR ROLE'),
            'a record variable reused for a third Get() after the row was deleted must not see a ghost row');
    end;
}
