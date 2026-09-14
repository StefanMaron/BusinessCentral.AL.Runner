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
