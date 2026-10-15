// Issue #2296 — the runner's synthetic session user must exist as a row in User (2000000120).
//
// LEAVE-BEHIND (#3269): SurSessionUserHasItsUserPropertyCompanionRow moved upstream to corpus
// codeunit 61203 "Test User Property Session Usr" (al-language-onprem app), because "the session
// user has a User Property (2000000121) row" is plain BC behaviour. The three tests below stay:
// they pin the runner's own TESTUSER / {C0A1BDFA-...} identity, and the precompiled relation
// check the runner seed must satisfy without switching validation off.
//
// RUNNER-MECHANISM claim. "The session user is a row in the User table, so a TableRelation to
// User.'User Security ID' resolves for UserSecurityId()" is plain BC behaviour and is asserted
// UPSTREAM, in the al-language corpus, where a real BC service tier adjudicates it. What is
// runner-specific — and therefore lives here — is WHICH user that is: AlRunner/BcRuntime.cs
// builds a skeleton NavUser with userName = 'TESTUSER' and the deterministic security id
// {C0A1BDFA-0000-0000-0000-545553545553}, and those two literals are runner inventions that no
// service tier can confirm. Before the fix the identity existed only in session state: AL was
// handed that GUID, stored it, and then AL's own referential check could not find the user
// anywhere, so every write to a field relating to User."User Security ID" died with
//
//     NavCSideValidateTableRelationException: The field User SID of table User Personalization
//     contains a value ({C0A1BDFA-0000-0000-0000-545553545553}) that cannot be found in the
//     related table (User)
//
// Measured on Microsoft's Tests-SMB bucket (BC 28.1.49838.53910, --test-data): 62 tests failed
// on exactly that message.
//
// "User Personalization" is used as the relating table deliberately rather than a table this
// bundle declares itself: it is a PRECOMPILED platform table whose "User SID" carries
// TableRelation = User."User Security ID", which is the shape the bucket run actually failed on.
// A relation declared in this bundle's own AL source would exercise the AL-source parser instead
// and could pass while the real one still failed.
//
// #2352 adds the session user's WINDOWS SID, the same kind of runner invention: TESTUSER is a
// Windows-authenticated user whose Sid() is S-1-5-21-1413829460-1431520594-0-1000 (the ASCII bytes
// of "TEST" and "USER"), and whose User row carries it in "Windows Security ID". Before it, Sid() was
// '' for every name, so the Microsoft idiom `User.SetRange("Windows Security ID", Sid(UserId))`
// matched the blank-SID set and FindFirst picked a user by random guid order (Codeunit134302).
//
// SurRelationRefusesAnUnknownSecurityId is the negative control, and it PASSED before the fix.
// It is what stops "insert the missing row" from being quietly replaced by "stop validating":
// a security id that belongs to nobody must still be refused.
codeunit 65591 "SUR Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "SUR Assert";
        RunnerUserName: Label 'TESTUSER', Locked = true;
        RunnerUserSid: Label '{C0A1BDFA-0000-0000-0000-545553545553}', Locked = true;
        UnknownSid: Label '{DEADBEEF-1111-2222-3333-444455556666}', Locked = true;
        RunnerWindowsSid: Label 'S-1-5-21-1413829460-1431520594-0-1000', Locked = true;
        EarlierThanAnyRandomGuid: Label '{00000000-0000-0000-0000-000000000001}', Locked = true;

    [Test]
    procedure SurSessionUserIsARowInTheUserTable()
    var
        UserRec: Record User;
    begin
        // The runner hands AL this security id through UserSecurityId(); the database must
        // contain the same one. Get(), not FindFirst(), so the row is reached by primary key.
        Assert.IsTrue(
            UserRec.Get(UserSecurityId()),
            'the session user must be a row in the User table, keyed by UserSecurityId()');

        // Concrete values, not "non-empty": the seed must be the identity BcRuntime already
        // put on the skeleton NavSession, not some other user that happens to be present.
        Assert.AreEqual(RunnerUserSid, Format(UserRec."User Security ID"), 'User."User Security ID"');
        Assert.AreEqual(RunnerUserName, UserRec."User Name", 'User."User Name"');
        Assert.AreEqual(UserId(), UserRec."User Name", 'User."User Name" must be what UserId() returns');
        // Format() of an Option FIELD answers the member name, while Format() of an option
        // MEMBER reference answers its ordinal, so the literal is the one that compares.
        Assert.AreEqual('Enabled', Format(UserRec.State), 'User.State');
    end;

    [Test]
    procedure SurSessionUserHasAWindowsSidAndItsRowCarriesIt()
    var
        UserRec: Record User;
    begin
        Assert.AreEqual(RunnerWindowsSid, Database.Sid(''), 'Sid() is the session user''s Windows SID');
        UserRec.Get(UserSecurityId());
        Assert.AreEqual(RunnerWindowsSid, UserRec."Windows Security ID", 'the session user''s row carries the same SID');
    end;

    [Test]
    procedure SurSidOfTheSessionUserNameIsTheSessionSidAndOtherNamesStayUnmapped()
    begin
        Assert.AreEqual(Database.Sid(''), Database.Sid(UserId()), 'Sid(UserId()) = Sid()');
        // Account names compare without case, as NTAccount does.
        Assert.AreEqual(RunnerWindowsSid, Database.Sid(LowerCase(UserId())), 'Sid(<session name, other case>)');
        // Negative direction: only the session user's name maps; any other name is still BC's not-mapped answer.
        Assert.AreEqual('', Database.Sid('SUR-NOBODY'), 'Sid(<unknown name>)');
        Assert.AreEqual('', Database.Sid(UserId() + 'X'), 'Sid(<name that merely starts like the session user''s>)');
    end;

    [Test]
    procedure SurUserNameOfASidIsTheInverseOfSid()
    var
        IdentityManagement: Codeunit "Identity Management";
    begin
        // User Card's OnAfterGetRecord asks for the name behind a row's Windows Security ID
        // (Codeunit132903); BC's ALDatabase.UserName(sid) raised the platform's NavUserNotFoundException here.
        Assert.AreEqual(UserId(), IdentityManagement.UserName(Database.Sid(UserId())), 'UserName(Sid(UserId())) is the session user');
        // Negative direction: a SID that belongs to nobody comes back unchanged, which is BC's own
        // not-mapped answer, and does not raise.
        Assert.AreEqual('S-1-5-21-1-2-3-4', IdentityManagement.UserName('S-1-5-21-1-2-3-4'), 'UserName(<unmapped SID>)');
    end;

    [Test]
    procedure SurUserNameOfAMalformedSidRaisesAsBcDoes()
    var
        IdentityManagement: Codeunit "Identity Management";
    begin
        // BC builds a SecurityIdentifier from the text first, and any failure of that, a malformed
        // sid included, is raised as NavUserNotFoundException; only a well-formed sid BC cannot
        // map comes back unchanged (the control in SurUserNameOfASidIsTheInverseOfSid).
        asserterror IdentityManagement.UserName('not-a-sid');
        Assert.ExpectedError(
            'Cannot retrieve the requested user name. The following error was reported: Value was invalid. (Parameter ''sddlForm'')');
    end;

    [Test]
    procedure SurWindowsSidLookupFindsTheSessionUserNotAUserWithoutOne()
    var
        StubUser: Record User;
        UserRec: Record User;
    begin
        // The Microsoft setup idiom (Codeunit134302 SetupUsersForApproval): a stub user without a
        // Windows SID is inserted, then the session user is found by its SID. The stub's key sorts
        // BEFORE the session user's, so a SID filter that degenerates to '' returns the stub.
        StubUser.Init();
        Evaluate(StubUser."User Security ID", EarlierThanAnyRandomGuid);
        StubUser.Insert();

        UserRec.SetRange("Windows Security ID", Database.Sid(UserId()));
        Assert.AreEqual(1, UserRec.Count(), 'exactly the session user carries the session user''s SID');
        UserRec.FindFirst();
        Assert.AreEqual(UserSecurityId(), UserRec."User Security ID", 'FindFirst by the session SID is the session user');
        Assert.AreEqual(RunnerUserName, UserRec."User Name", 'and not the stub');

        // Control: the blank filter still finds the stub, so the filter above is not matching by accident.
        Clear(UserRec);
        UserRec.SetRange("Windows Security ID", '');
        UserRec.FindFirst();
        Assert.AreEqual(Format(StubUser."User Security ID"), Format(UserRec."User Security ID"), 'a blank SID matches the SID-less stub');
    end;

    [Test]
    procedure SurRelationToUserSecurityIdAcceptsTheSessionUser()
    var
        UserPersonalization: Record "User Personalization";
    begin
        // "User SID" carries TableRelation = User."User Security ID" on a table this bundle
        // never compiled, so this is the precompiled-relation path the bucket run failed on.
        UserPersonalization.Init();
        UserPersonalization.Validate("User SID", UserSecurityId());

        Assert.AreEqual(
            RunnerUserSid, Format(UserPersonalization."User SID"),
            'Validate must keep the session user security id it was handed');
    end;

    [Test]
    procedure SurRelationRefusesAnUnknownSecurityId()
    var
        UserPersonalization: Record "User Personalization";
        Unknown: Guid;
    begin
        Unknown := UnknownSid;
        UserPersonalization.Init();

        // The negative direction: seeding the session user must not have turned the relation
        // check off. A security id that belongs to no user is still refused.
        asserterror UserPersonalization.Validate("User SID", Unknown);
        Assert.ExpectedError('cannot be found in the related table');
    end;
}
