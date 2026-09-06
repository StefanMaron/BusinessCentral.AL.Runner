codeunit 70521 "SURC Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        // The security id the Install trigger put on the stand-in row. Everything below turns
        // on this CONCRETE value: after adoption it is what UserSecurityId() returns, and it is
        // a value that came out of the data rather than out of the runner.
        AdoptedSidTok: Label '{A17E9C42-5B08-4D6F-9E31-0C7A2F84B155}', Locked = true;
        // What BcRuntime generates for the skeleton session when nothing is adopted. Asserted
        // NOT to be the answer here, and asserted TO be the answer in the sibling fixture
        // SessionUserRowAlreadyPresent -- which is what stops a constant-returning
        // implementation satisfying both.
        GeneratedSidTok: Label '{C0A1BDFA-0000-0000-0000-545553545553}', Locked = true;
        BackupUserTok: Label 'STANDS-IN-FOR-A-BACKUP-USER', Locked = true;

    [Test]
    procedure SurcTheSameNamedForeignUserIsInTheTable()
    var
        UserRec: Record User;
        AdoptedSid: Guid;
    begin
        // Pins the PRECONDITION. Without it, the tests below would also pass if the Install
        // trigger had quietly written nothing, and would then prove nothing about a collision.
        Evaluate(AdoptedSid, AdoptedSidTok);
        if not UserRec.Get(AdoptedSid) then
            Error('the Install trigger''s stand-in backup user must be a row in User');
        if UserRec."User Name" <> UserId() then
            Error(
              'the stand-in user must carry the session user''s own name: expected "%1", got "%2"',
              UserId(), UserRec."User Name");
        if UserRec."Full Name" <> BackupUserTok then
            Error('User."Full Name" is "%1", expected "%2"', UserRec."Full Name", BackupUserTok);
    end;

    [Test]
    procedure SurcTheSessionAdoptedTheExistingRowsSecurityId()
    var
        AdoptedSid: Guid;
        GeneratedSid: Guid;
    begin
        // THE #2983 DISCRIMINATOR, and the one the maintainer's decision turns on.
        //
        // A different user already carries this session's user name. Real BC will not hold two
        // users of one name -- SystemTableTriggers.OnBeforeInsertAsync's `case 2000000120:` arm
        // calls IsUserFieldUniqueAsync(recordBuffer, 2, insert: true) and raises before writing,
        // and AlRunner/Patches/UserTableTriggerPatches.cs reproduces that -- so the runner's
        // session-user seed cannot write its own row here.
        //
        // What it does instead is ADOPT: the session takes the existing row's security id as its
        // own, so UserSecurityId() answers with a value that came from the data. The first
        // implementation of #2983 refused instead, leaving the session as a user present in no
        // row at all, which is the state #2296 exists to remove. Against that implementation
        // this test fails, because UserSecurityId() is still the generated id below.
        Evaluate(AdoptedSid, AdoptedSidTok);
        Evaluate(GeneratedSid, GeneratedSidTok);

        if UserSecurityId() <> AdoptedSid then
            Error(
              'UserSecurityId() must be the ADOPTED id %1 (the one the data already held for this '
              + 'user name), but it is %2', AdoptedSidTok, Format(UserSecurityId()));

        // Named separately so a failure says WHICH way it went wrong: still generated (nothing
        // adopted) reads differently from adopted-something-else.
        if UserSecurityId() = GeneratedSid then
            Error(
              'UserSecurityId() is still the runner-generated id %1, so no adoption happened',
              GeneratedSidTok);
    end;

    [Test]
    procedure SurcTheSessionUserResolvesToTheRowTheDataProvided()
    var
        UserRec: Record User;
        AdoptedSid: Guid;
    begin
        // The point of adopting rather than refusing: the session user is a real row again, so
        // every TableRelation to User."User Security ID" resolves the id UserSecurityId()
        // reports. That is #2296's whole subject.
        if not UserRec.Get(UserSecurityId()) then
            Error(
              'after adoption the session user must be a row in User -- a session whose user is '
              + 'in no row is the state AlRunner#2296 exists to remove');

        // ...and it is the DATA's row, not one the seed wrote anyway. Full Name is the marker:
        // the runner's seed writes the skeleton NavUser's full name ("TESTUSER"), never this.
        if UserRec."Full Name" <> BackupUserTok then
            Error(
              'the session user must resolve to the stand-in row (Full Name "%1"), but Full Name '
              + 'is "%2" -- the seed wrote a row instead of adopting one',
              BackupUserTok, UserRec."Full Name");

        Evaluate(AdoptedSid, AdoptedSidTok);
        if UserRec."User Security ID" <> AdoptedSid then
            Error('the resolved row must be the stand-in %1', AdoptedSidTok);
    end;

    [Test]
    procedure SurcAdoptionAddedNoSecondRowAndLeftUserIdAlone()
    var
        Probe: Record User;
    begin
        // Adoption writes NOTHING to the User table. Two rows under one name is the state real
        // BC refuses to hold, and it is what the pre-#2983 runner was left with.
        Probe.SetRange("User Name", UserId());
        if Probe.Count() <> 1 then
            Error('exactly one row may carry the user name "%1", found %2', UserId(), Probe.Count());

        // Only the security id is adopted. The NAME is the key the adoption matched ON, so it
        // must be unchanged -- an implementation that took the whole identity off the row would
        // pass the sid assertions above and be wrong here.
        if Probe.FindFirst() then
            if Probe."User Name" <> UserId() then
                Error('UserId() must still be "%1"', Probe."User Name");
    end;

    [Test]
    procedure SurcTheAdoptedUserHasItsUserPropertyRow()
    var
        UserProperty: Record "User Property";
    begin
        // BC creates a User Property (2000000121) row with every User, and
        // UserManagement.DirectSetUserFieldValue does a RAISING Get on it for the session user
        // (#2355). An adopted row reached the table without passing through the runner's insert
        // prepend, so the seed re-establishes that invariant for the id it adopted.
        //
        // LIMITED CLAIM, stated so nobody reads more into a pass: in THIS fixture the row comes
        // from the Install trigger's own Insert, which does go through the prepend. So what this
        // pins is that adoption does not LOSE the companion row, not that it can create one from
        // nothing -- AL has no way to write a User row that bypasses the prepend.
        if not UserProperty.Get(UserSecurityId()) then
            Error(
              'the adopted session user (%1) must have a User Property row, or Microsoft AL '
              + 'reaching NavUserAccountHelper.SetAuthenticationObjectId fails the AlRunner#2355 way',
              Format(UserSecurityId()));
    end;
}
