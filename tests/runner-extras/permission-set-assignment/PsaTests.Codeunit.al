// Issue #3039 — "is this permission set assigned to this user" must be ANSWERED, not NRE.
//
// RUNNER-MECHANISM claim. The Base Application / System Application behaviour exercised here is
// plain BC and needs no corpus test: codeunit 153's `IsSuper` asks the platform whether SUPER is
// assigned, and codeunit 9002's User/OnBeforeModifyEvent subscriber refuses to modify SOMEONE
// ELSE'S user row unless the caller can manage users. What is asserted below is the runner's own
// permission model, which no service tier can adjudicate because a service tier HAS a permission
// database and the runner does not:
//
//   * `NavSession.Permissions` is null on the skeleton session, so BC's
//     PermissionManagement.IsPermissionSetAssignedAsync dereferenced null and every one of these
//     paths raised NavNCLDotNetInvokeException instead of returning a boolean.
//   * The runner answers from the Access Control table (2000000053) — the same table real BC
//     builds its permission cache from, and ordinary AL-writable storage here.
//   * Plus exactly one stated fact: the skeleton session runs as SUPER. That is the position the
//     runner already ships in NavSession.HasExecutePermission* (→ true),
//     NavSession.VerifyExecutePermission (→ no-op) and NavSession.MaximizePermissions (→ no-op),
//     each justified in-tree with the same sentence.
//
// See AlRunner/Patches/RecordPatches.PermissionSetAssignment.cs for the mechanism.
//
// WHY IT WAS INVISIBLE UNTIL NOW: codeunit 9002's subscriber is an async ValueTask state machine,
// and BC's `memberId == 0` dispatch branch discarded the returned task (#2932), so the body was
// abandoned at its first suspension and never reached IsSuper. PR #2979 made it run to completion
// and this was the next thing in its path.
codeunit 65612 "PSA Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "PSA Assert";
        SuperTok: Label 'SUPER', Locked = true;

    local procedure NewUser(var User: Record User; Name: Code[50]): Guid
    begin
        User.Init();
        User."User Security ID" := CreateGuid();
        User."User Name" := Name;
        User.Insert(false);
        exit(User."User Security ID");
    end;

    local procedure GrantPermissionSet(UserSid: Guid; RoleId: Code[20]; CompanyName: Text[30])
    var
        AccessControl: Record "Access Control";
        NullGuid: Guid;
    begin
        AccessControl.Init();
        AccessControl."User Security ID" := UserSid;
        AccessControl."Role ID" := RoleId;
        AccessControl."Company Name" := CompanyName;
        AccessControl."App ID" := NullGuid;
        AccessControl.Scope := AccessControl.Scope::System;
        AccessControl.Insert(false);
    end;

    // ── THE REPORTED DEFECT ────────────────────────────────────────────────────────────────
    // Codeunit 9002's CheckCurrentUserCanModifyUser reaches its permission check only for a row
    // that is NOT the session's own user (its own AL exits early when the security ids match),
    // which is exactly what this does. Before the fix this raised
    //   NavNCLDotNetInvokeException: A call to ...NavUserAccountHelper.IsPermissionSetAssigned
    //   failed with this message: Object reference not set to an instance of an object.
    [Test]
    procedure ModifyingAnotherUsersRow_Succeeds_BecauseTheSessionUserIsSuper()
    var
        User: Record User;
        OtherSid: Guid;
        Reread: Record User;
    begin
        OtherSid := NewUser(User, 'PSA-OTHER-USER');
        Assert.IsTrue(OtherSid <> UserSecurityId(),
            'precondition: the row must belong to someone other than the session user, or '
            + 'codeunit 9002 exits before the permission check');

        User."Full Name" := 'Renamed By PSA';
        User.Modify(true);

        // Assert the WRITE landed, not merely that nothing was raised: a Modify that silently
        // did nothing would satisfy "no error" just as well.
        Assert.IsTrue(Reread.Get(OtherSid), 'the modified row must still be readable');
        Assert.AreEqual('Renamed By PSA', Reread."Full Name", 'the modification must have been written');
    end;

    // The negative half of the same subscriber, so a fix that neutered codeunit 9002 outright
    // could not pass the test above. The blank User Name is refused BEFORE the permission check.
    [Test]
    procedure ModifyingAUserWithABlankName_StillRaisesTheSubscribersError()
    var
        User: Record User;
    begin
        NewUser(User, '');
        Assert.AreEqual('', User."User Name", 'precondition: the row must have a blank User Name');

        asserterror User.Modify(true);

        Assert.ExpectedError('User Name must have a value', GetLastErrorText());
    end;

    // ── THE PERMISSION ANSWER ITSELF ──────────────────────────────────────────────────────
    // The session user is SUPER. The IsEmpty precondition matters: codeunit 153's IsSuper opens
    // with `if User.IsEmpty() then exit(true)` — the "no users provisioned yet" bootstrap — so
    // without it this test would pass against an empty User table without the platform ever
    // being asked the question.
    [Test]
    procedure TheSessionUser_IsSuper()
    var
        UserPermissions: Codeunit "User Permissions";
        User: Record User;
    begin
        Assert.IsFalse(User.IsEmpty(),
            'precondition: the User table must be non-empty, or codeunit 153 answers true from '
            + 'its bootstrap branch without asking about permission sets at all');

        Assert.IsTrue(UserPermissions.IsSuper(UserSecurityId()), 'the session user must be SUPER');
    end;

    // Guards against a blanket `true`. A user who exists but holds no permission set is not
    // SUPER, and the runner must say so.
    [Test]
    procedure AUserWithNoAccessControlRow_IsNotSuper()
    var
        UserPermissions: Codeunit "User Permissions";
        User: Record User;
        OtherSid: Guid;
        AccessControl: Record "Access Control";
    begin
        OtherSid := NewUser(User, 'PSA-NO-ROLES');
        AccessControl.SetRange("User Security ID", OtherSid);
        Assert.IsTrue(AccessControl.IsEmpty(), 'precondition: this user must hold no permission set');

        Assert.IsFalse(UserPermissions.IsSuper(OtherSid),
            'a user with no Access Control row must not be SUPER');
    end;

    // Guards against a blanket `false`, and proves the answer is read from real, AL-writable
    // state rather than from a hardcoded verdict about who is SUPER.
    [Test]
    procedure AUserGrantedSuperInAccessControl_IsSuper()
    var
        UserPermissions: Codeunit "User Permissions";
        User: Record User;
        OtherSid: Guid;
    begin
        OtherSid := NewUser(User, 'PSA-GRANTED-SUPER');
        Assert.IsFalse(UserPermissions.IsSuper(OtherSid),
            'precondition: this user must not be SUPER before the grant, or the grant proves nothing');

        GrantPermissionSet(OtherSid, SuperTok, '');

        Assert.IsTrue(UserPermissions.IsSuper(OtherSid),
            'an Access Control row granting SUPER must make that user SUPER');
    end;

    // The Role ID is really compared: granting a DIFFERENT permission set must not answer SUPER.
    // Without this, "any Access Control row at all → true" would pass the test above.
    [Test]
    procedure AUserGrantedSomeOtherPermissionSet_IsNotSuper()
    var
        UserPermissions: Codeunit "User Permissions";
        User: Record User;
        OtherSid: Guid;
        AccessControl: Record "Access Control";
    begin
        OtherSid := NewUser(User, 'PSA-GRANTED-BASIC');
        GrantPermissionSet(OtherSid, 'D365 BASIC', '');

        AccessControl.SetRange("User Security ID", OtherSid);
        Assert.IsFalse(AccessControl.IsEmpty(), 'precondition: the grant must have been written');

        Assert.IsFalse(UserPermissions.IsSuper(OtherSid),
            'a permission set other than SUPER must not answer the SUPER question');
    end;
}
