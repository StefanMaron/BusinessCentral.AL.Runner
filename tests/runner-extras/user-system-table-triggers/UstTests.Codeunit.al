// Issues #2983 and #2356 — BC's SystemTableTriggers arms for the User system table.
//
// WHY THESE LIVE HERE AND NOT UPSTREAM IN THE CORPUS
//   .claude/rules/bc-behavior-tests-go-upstream.md says a claim a service tier can adjudicate
//   must go upstream, and every claim below is one. The blocker is not "no container": it is
//   that MEASURING these claims on a live tier damages the tier for everything after them.
//   Microsoft's own shipped test code says so, in
//   BaseApp/Test/Tests-SINGLESERVER/TestAppPermissions.Codeunit.al (codeunit 134614), whose
//   CleanupData carries this comment verbatim:
//
//       // When we add any user into User table Server switches authentication mode
//       // and further tests fail with permission error until Server is restarted.
//       // Automatic rollback in test isolation does not revert Server's authentication mode.
//
//   The corpus runs sixteen legs on real service tiers. A corpus test that inserted a user and
//   then failed before reaching its own cleanup would take the rest of its leg down with it, and
//   the failure would read as unrelated. That is not a trade worth making for a claim BC's own
//   shipped IL already states unambiguously.
//
// WHAT THE BC-SIDE CLAIM RESTS ON INSTEAD (Ncl.dll, BC 28.1; identical shape on 27.0-28.4)
//   SystemTableTriggers.OnBeforeInsertAsync, `case 2000000120:`
//       if (!(await IsUserFieldUniqueAsync(recordBuffer, 2, insert: true)))
//           throw NavNCLUserTableUserNameMustBeUniqueException.Create();
//       ... field 7, only when non-empty:
//           throw NavNCLUserTableUserWindowsSidMustBeUniqueException.Create();
//   SystemTableTriggers.OnAfterDeleteAsync, `case 2000000120:`
//       await DeleteAllFromTableAsync(session, 2000000053, 1, userSid);                        // Access Control
//       await DeleteAllFromTableWithMaximizedPermissionAsync(session, 2000000121, 1, userSid);  // User Property
//       await DeleteAllFromTableWithMaximizedPermissionAsync(session, 2000000107, 4, userSid);  // Isolated Storage
//       await DeleteAllFromTableWithMaximizedPermissionAsync(session, 2000000233, 5, userSid);  // Tenant Report Layout Selection
//
//   Both arms are reached through TransactionalDataCache, which the runner's store
//   (CreateTempDataAccess) is not, so neither ran here at all.
//
//   The runner raises BC's OWN exception types through BC's own static factories, so
//   'The user name must be unique.' asserted below is BC's message text, not a paraphrase this
//   suite and the runner agreed on between themselves.
//
// THE CONTROLS
//   Each refusal is paired with the case that must still be ACCEPTED, so none of these can be
//   satisfied by refusing more: a second user with a different name inserts; two users with
//   empty Windows SIDs both insert; and deleting one user leaves another user's dependent rows
//   exactly where they were.
codeunit 65621 "UST Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "UST Assert";
        UniqueNameErr: Label 'The user name must be unique.', Locked = true;
        SharedNameTok: Label 'UST-SHARED-NAME', Locked = true;
        FirstNameTok: Label 'UST-FIRST', Locked = true;
        SecondNameTok: Label 'UST-SECOND', Locked = true;
        WinSidTok: Label 'S-1-5-21-1111111111-2222222222-3333333333-1001', Locked = true;

    local procedure NewUser(var UserRec: Record User; Name: Text): Guid
    var
        Sid: Guid;
    begin
        Sid := CreateGuid();
        UserRec.Init();
        UserRec."User Security ID" := Sid;
        UserRec."User Name" := CopyStr(Name, 1, MaxStrLen(UserRec."User Name"));
        UserRec.Insert();
        exit(Sid);
    end;

    // ── #2983: the insert arm's uniqueness refusals ─────────────────────────────────────

    [Test]
    procedure UstASecondUserWithTheSameNameIsRefused()
    var
        FirstUser: Record User;
        SecondUser: Record User;
        Probe: Record User;
        Reader: Record User;
        SecondSid: Guid;
    begin
        NewUser(FirstUser, SharedNameTok);
        // Commit so the assertions after `asserterror` have a defined state to read. An error
        // rolls the write transaction back to the last commit point -- BC's behaviour, and the
        // runner's -- so without this the FIRST user's insert is rolled back too and the count
        // below reads 0 for a reason that has nothing to do with uniqueness. MEASURED: it read
        // 0 before this line was here.
        Commit();

        SecondSid := CreateGuid();
        SecondUser.Init();
        SecondUser."User Security ID" := SecondSid;
        SecondUser."User Name" := SharedNameTok;

        // BC raises here rather than returning false: DataError.TrapError converts DATA errors
        // (a key violation, a missing row), and a system-table trigger's refusal is not one. So
        // `if not User.Insert() then` does not take its false branch on a real tier either.
        asserterror SecondUser.Insert();
        Assert.ExpectedError(UniqueNameErr, GetLastErrorText());

        Probe.SetRange("User Name", SharedNameTok);
        Assert.AreEqual(1, Probe.Count(), 'exactly one user may carry a given user name');
        Assert.IsFalse(
            Reader.Get(SecondSid),
            'the refused row must not be in the table under its own security id either');
    end;

    [Test]
    procedure UstASecondUserWithADifferentNameIsAccepted()
    // The control for the test above. Without it, a runner that refused EVERY second user
    // insert would pass, and the claim would be about nothing.
    var
        FirstUser: Record User;
        SecondUser: Record User;
        Reader: Record User;
        FirstSid: Guid;
        SecondSid: Guid;
    begin
        FirstSid := NewUser(FirstUser, FirstNameTok);
        SecondSid := NewUser(SecondUser, SecondNameTok);

        Assert.IsTrue(Reader.Get(FirstSid), 'the first user must still be reachable by its own id');
        Assert.AreEqual(FirstNameTok, Reader."User Name", 'the first user''s name');
        Assert.IsTrue(Reader.Get(SecondSid), 'the second user must be reachable by its own id');
        Assert.AreEqual(SecondNameTok, Reader."User Name", 'the second user''s name');
    end;

    [Test]
    procedure UstASecondUserWithTheSameWindowsSecurityIdIsRefused()
    var
        FirstUser: Record User;
        SecondUser: Record User;
        Reader: Record User;
        SecondSid: Guid;
    begin
        FirstUser.Init();
        FirstUser."User Security ID" := CreateGuid();
        FirstUser."User Name" := 'UST-WINSID-ONE';
        FirstUser."Windows Security ID" := WinSidTok;
        FirstUser.Insert();

        SecondSid := CreateGuid();
        SecondUser.Init();
        SecondUser."User Security ID" := SecondSid;
        // A DIFFERENT user name, so the only thing that can refuse this row is field 7.
        SecondUser."User Name" := 'UST-WINSID-TWO';
        SecondUser."Windows Security ID" := WinSidTok;

        asserterror SecondUser.Insert();
        Assert.IsFalse(
            Reader.Get(SecondSid),
            'a second user carrying an existing Windows Security ID must not be in the table');
    end;

    [Test]
    procedure UstTwoUsersWithNoWindowsSecurityIdAreBothAccepted()
    // The control for the Windows SID refusal. BC skips that check when field 7 is empty
    // (`navText2 != null && !navText2.IsZeroOrEmpty`), so an empty value is not a collision --
    // which matters, because almost every user the runner ever holds has one.
    var
        FirstUser: Record User;
        SecondUser: Record User;
        Reader: Record User;
        FirstSid: Guid;
        SecondSid: Guid;
    begin
        FirstSid := NewUser(FirstUser, 'UST-NOWINSID-ONE');
        SecondSid := NewUser(SecondUser, 'UST-NOWINSID-TWO');

        Assert.IsTrue(Reader.Get(FirstSid), 'the first user with no Windows SID must be present');
        Assert.AreEqual('', Reader."Windows Security ID", 'precondition: field 7 is empty');
        Assert.IsTrue(Reader.Get(SecondSid), 'the second user with no Windows SID must be present too');
        Assert.AreEqual('', Reader."Windows Security ID", 'precondition: field 7 is empty');
    end;

    // ── #2356: the delete arm's cascade ─────────────────────────────────────────────────

    [Test]
    procedure UstDeletingAUserTakesItsUserPropertyRow()
    var
        UserRec: Record User;
        UserProperty: Record "User Property";
        Sid: Guid;
    begin
        Sid := NewUser(UserRec, 'UST-DEL-PROPERTY');
        Assert.IsTrue(
            UserProperty.Get(Sid),
            'precondition: the insert arm creates the User Property companion row (#2355)');

        UserRec.Delete();

        Assert.IsFalse(
            UserProperty.Get(Sid),
            'deleting a User must take its User Property row with it');
    end;

    [Test]
    procedure UstDeletingAUserTakesItsAccessControlAndIsolatedStorageRows()
    var
        UserRec: Record User;
        AccessControl: Record "Access Control";
        IsolatedStorage: Record "Isolated Storage";
        Sid: Guid;
        AppId: Guid;
    begin
        Sid := NewUser(UserRec, 'UST-DEL-DEPENDENTS');

        AccessControl.Init();
        AccessControl."User Security ID" := Sid;
        AccessControl."Role ID" := 'UST-ROLE';
        AccessControl."Company Name" := CopyStr(CompanyName(), 1, MaxStrLen(AccessControl."Company Name"));
        AccessControl.Insert();

        AppId := CreateGuid();
        IsolatedStorage.Init();
        IsolatedStorage."App Id" := AppId;
        IsolatedStorage.Scope := IsolatedStorage.Scope::User;
        IsolatedStorage."User Id" := Sid;
        IsolatedStorage."Key" := 'UST-KEY';
        IsolatedStorage.Insert();

        UserRec.Delete();

        AccessControl.Reset();
        AccessControl.SetRange("User Security ID", Sid);
        Assert.AreEqual(
            0, AccessControl.Count(),
            'deleting a User must take its Access Control rows (2000000053, field 1)');

        IsolatedStorage.Reset();
        IsolatedStorage.SetRange("User Id", Sid);
        Assert.AreEqual(
            0, IsolatedStorage.Count(),
            'deleting a User must take its Isolated Storage rows (2000000107, field 4)');
    end;

    [Test]
    procedure UstDeletingOneUserLeavesAnotherUsersRowsAlone()
    // The control for the cascade. Without it, "delete everything in those four tables" would
    // pass every assertion above.
    var
        DoomedUser: Record User;
        KeptUser: Record User;
        UserProperty: Record "User Property";
        AccessControl: Record "Access Control";
        DoomedSid: Guid;
        KeptSid: Guid;
    begin
        DoomedSid := NewUser(DoomedUser, 'UST-KEEP-DOOMED');
        KeptSid := NewUser(KeptUser, 'UST-KEEP-KEPT');

        AccessControl.Init();
        AccessControl."User Security ID" := KeptSid;
        AccessControl."Role ID" := 'UST-KEPT-ROLE';
        AccessControl."Company Name" := CopyStr(CompanyName(), 1, MaxStrLen(AccessControl."Company Name"));
        AccessControl.Insert();

        DoomedUser.Delete();

        Assert.IsTrue(
            UserProperty.Get(KeptSid),
            'the other user''s User Property row must survive');
        Assert.IsFalse(
            UserProperty.Get(DoomedSid),
            'precondition: the deleted user''s own companion row is gone');

        AccessControl.Reset();
        AccessControl.SetRange("User Security ID", KeptSid);
        Assert.AreEqual(
            1, AccessControl.Count(),
            'the other user''s Access Control row must survive');
    end;

    [Test]
    procedure UstDeleteAllCascadesTheSameWayDeleteDoes()
    // AL binds `Rec.DeleteAll()` to ALDeleteAll(bool) -> DeleteAllAsync(bool), NOT to the
    // ALDeleteAsync entry point the cascade is prepended to -- so #2356 predicted this surface
    // would be missed. It is not, and the reason is in BC's own IL: DeleteAllAsync takes its
    // BULK path only when CanUseBulkDeleteAll holds, and that predicate ends in
    // !SystemTableTriggers.TableHasSystemDeleteTrigger(record), whose static switch lists
    // 2000000120. For User it is therefore always false, so DeleteAllAsync falls to its row
    // loop and calls ALDeleteAsync per row. This test measures that rather than trusting it.
    var
        UserRec: Record User;
        UserProperty: Record "User Property";
        Sid: Guid;
    begin
        Sid := NewUser(UserRec, 'UST-DELETEALL');
        Assert.IsTrue(UserProperty.Get(Sid), 'precondition: the companion row is there');

        UserRec.Reset();
        UserRec.SetRange("User Security ID", Sid);
        UserRec.DeleteAll();

        Assert.IsFalse(
            UserProperty.Get(Sid),
            'DeleteAll() must cascade exactly as Delete() does -- both funnel through ALDeleteAsync');
    end;
}
