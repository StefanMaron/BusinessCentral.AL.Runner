codeunit 70521 "SURC Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        CollidingSidTok: Label '{A17E9C42-5B08-4D6F-9E31-0C7A2F84B155}', Locked = true;
        BackupUserTok: Label 'STANDS-IN-FOR-A-BACKUP-USER', Locked = true;

    [Test]
    procedure SurcTheSameNamedForeignUserIsInTheTable()
    var
        UserRec: Record User;
        CollidingSid: Guid;
    begin
        // Pins the PRECONDITION. Without it, the test below would also pass if the Install
        // trigger had quietly written nothing, and would then prove nothing about a collision.
        Evaluate(CollidingSid, CollidingSidTok);
        if not UserRec.Get(CollidingSid) then
            Error('the Install trigger''s stand-in backup user must be a row in User');
        if UserRec."User Name" <> UserId() then
            Error(
              'the stand-in user must carry the session user''s own name: expected "%1", got "%2"',
              UserId(), UserRec."User Name");
        if UserRec."Full Name" <> BackupUserTok then
            Error('User."Full Name" is "%1", expected "%2"', UserRec."Full Name", BackupUserTok);
    end;

    [Test]
    procedure SurcTheSessionUserIsRefusedItsOwnRowOverTheDuplicateName()
    var
        UserRec: Record User;
        Probe: Record User;
        CollidingSid: Guid;
    begin
        // THIS TEST USED TO ASSERT THE OPPOSITE, and the flip is the point of the fixture.
        //
        // Before AlRunner#2983 the seed landed here and the run was left holding TWO rows
        // sharing a user name, which real BC would refuse. The mechanism was an index on
        // neither side: the runner's store is BC's own CreateTempDataAccess, which enforces the
        // PRIMARY key on "User Security ID" and nothing else, and real BC refuses the duplicate
        // NAME from a trigger -- SystemTableTriggers.OnBeforeInsertAsync's `case 2000000120:`
        // arm calls IsUserFieldUniqueAsync(recordBuffer, 2, insert: true) and throws
        // NavNCLUserTableUserNameMustBeUniqueException.Create() before writing.
        //
        // AlRunner/Patches/UserTableTriggerPatches.cs reproduces that arm now, so the seed is
        // refused, and RecordPatches.EnsureUserSystemTableRowSeeded's Refused branch -- built by
        // #2941 and until now reachable only from the exception path -- is what handles it. The
        // seed's own stderr line names the exception; SessionUserRowRefusalTests asserts it.
        if UserRec.Get(UserSecurityId()) then
            Error(
              'the seed must be REFUSED here: a different user already carries this user name, '
              + 'which is a state real BC cannot hold either. A row present under '
              + 'UserSecurityId() means the uniqueness arm of #2983 stopped running.');

        // The refusal must leave the table the way BC would: exactly one row under that name,
        // and it is the fixture's stand-in, not a second row the seed managed to write anyway.
        Probe.SetRange("User Name", UserId());
        if Probe.Count() <> 1 then
            Error('exactly one row may carry the user name "%1", found %2', UserId(), Probe.Count());

        Evaluate(CollidingSid, CollidingSidTok);
        if not Probe.FindFirst() then
            Error('the stand-in row must still be there after the refusal');
        if Probe."User Security ID" <> CollidingSid then
            Error(
              'the surviving row must be the stand-in (%1), not one the seed wrote (%2)',
              CollidingSidTok, Format(Probe."User Security ID"));
        if Probe."Full Name" <> BackupUserTok then
            Error('the surviving row must be the stand-in, told apart by Full Name');
    end;
}
