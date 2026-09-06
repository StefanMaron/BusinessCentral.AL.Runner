/// Regression/contract suite for issue #2411. See app.json for the full mechanism writeup,
/// including why `WarehouseEmployeeFirstTouchViaTestPage_InsertRaisesOnBeforeInsertEvent`
/// below is a REGRESSION GUARD, not a RED/GREEN proof of the TestPageFactory
/// .TryBuildBlankRecord diff: it is green with that fix removed too, because BC's own
/// SetSourceTable/NewRecordAsync machinery already wires the subscriber via one of #2412's
/// three already-fixed sites (xRec construction) before any live TestPage's Insert can
/// dispatch. It stays here because "a table-level trigger subscriber on a table first touched
/// via a TestPage eventually fires" is still a real, worth-guarding end-to-end claim.
codeunit 65264 "Ttit Trigger Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Ttit Assert";

    [Test]
    procedure WarehouseEmployeeFirstTouchViaTestPage_InsertRaisesOnBeforeInsertEvent()
    var
        WhseEmployeePage: TestPage "Warehouse Employees";
    begin
        // Warehouse Employee's NCLMetaTable does not exist anywhere in this process before
        // this line runs -- it is built LAZILY, right here, by TestPageFactory
        // .TryBuildBlankRecord, when the TestPage opens on a new row. See app.json /
        // TtitSub.Codeunit.al: this does NOT discriminate the #2411 fix (BC's own
        // SetSourceTable/NewRecordAsync already wires the subscriber via xRec construction,
        // one of #2412's three already-fixed sites, before Insert ever dispatches on this
        // record) -- it is a regression guard for the end-to-end contract, not a proof.
        WhseEmployeePage.OpenNew();
        // UserId(), not a made-up name. Warehouse Employee."User ID" OnValidate calls codeunit
        // 9843 "User Selection".ValidateUserName, whose AL is
        //
        //     if UserName = '' then exit;
        //     if User.IsEmpty() then exit;               // the WHOLE User table
        //     User.SetRange("User Name", UserName);
        //     if User.IsEmpty() then Error(UserNameDoesNotExistErr, UserName);
        //
        // so it only skips the check while the User table is entirely empty. It used to be,
        // because the runner never put its own session user in it (#2296); this line said
        // 'TTITWHSE1' and Microsoft's guard let it through. With the session user seeded the
        // table is no longer empty and Microsoft's own code refuses an unknown name — which is
        // what real BC does, so the name has to be a real one. Nothing about this suite's claim
        // (that the OnBeforeInsertEvent subscriber fires) depends on which name it is.
        WhseEmployeePage."User ID".SetValue(UserId());

        // A TestPage's new row is a client-side draft -- SetValue only writes the buffer, and
        // the row is not actually inserted until the cursor leaves it (or the page closes):
        // LiveNavTestPage.FlushPendingNewRow's Record.ALInsertAsync(RunTrigger: true), the same
        // dispatch a bare Record.Insert(true) would use, so OnBeforeInsertEvent fires here,
        // before Warehouse Employee's own OnInsert business logic ever runs.
        asserterror WhseEmployeePage.Close();
        Assert.ExpectedError('WAREHOUSE EMPLOYEE OnBeforeInsertEvent FIRED');
    end;

    [Test]
    procedure UnrelatedTableWithNoSubscriber_InsertViaTestPageSucceeds()
    var
        SalespersonRec: Record "Salesperson/Purchaser";
        SalespersonPage: TestPage "Salesperson/Purchaser Card";
    begin
        // Negative: "Salesperson/Purchaser" has no OnBeforeInsertEvent subscriber anywhere in
        // this bundle. This is what rules out a no-op fix that makes every TestPage-driven
        // insert on a lazily-built table raise, rather than one that wires the specific
        // subscribed (table, event) pair.
        SalespersonPage.OpenNew();
        SalespersonPage.Code.SetValue('TTIT-SP-1');
        SalespersonPage.Name.SetValue('Ttit Salesperson');
        SalespersonPage.Close();

        Assert.IsTrue(SalespersonRec.Get('TTIT-SP-1'),
            'the row must actually be inserted -- the TestPage-driven Insert must not have been intercepted by some unrelated error path');
    end;
}
