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
    procedure SurcTheSessionUserStillGetsItsOwnRow()
    var
        UserRec: Record User;
    begin
        // MEASURED, and it is not what the #2941 review predicted. The review expected a unique
        // key on User."User Name" to refuse this seed and silently defeat #2296. The seed lands
        // anyway, and the mechanism is not an index on either side:
        //
        //   * the runner's store here is BC's own CreateTempDataAccess, which enforces the
        //     PRIMARY key on "User Security ID" and nothing else;
        //   * real BC refuses the duplicate name from a TRIGGER --
        //     SystemTableTriggers.OnBeforeInsertAsync's `case 2000000120:` arm validates a unique
        //     user name before writing -- and AlRunner/Patches/UserTableTriggerPatches.cs's own
        //     header records that the runner reproduces only that arm's User Property companion
        //     insert and that "None of that [validation] is reproduced here".
        //
        // So the session user is reachable by its own id even with a same-named foreign user
        // present.
        //
        // The count is deliberately NOT asserted here. Two rows sharing a user name is a real
        // divergence from BC, which would refuse the second one -- but this suite should not
        // bless it by writing the wrong number into an assertion. Tracked as AlRunner#2983.
        //
        // WHEN THAT GAP IS FIXED, THIS TEST WILL START FAILING -- and that is the point. At that
        // moment the seed genuinely is refused, RecordPatches' Refused branch becomes reachable
        // for the first time, and whoever lands the fix has to decide what the seed should do
        // about it. A silent pass here would hide that decision.
        if not UserRec.Get(UserSecurityId()) then
            Error(
              'the session user has no row of its own. If AlRunner#2983 has just been fixed -- by '
              + 'reproducing BC''s OnBeforeInsertAsync validation for table 2000000120, or by '
              + 'enforcing uniqueness in the store -- this is expected: the seed is now refused '
              + 'over the duplicate user name, and '
              + 'RecordPatches.EnsureUserSystemTableRowSeeded''s Refused branch is what handles '
              + 'it. See AlRunner#2296 and #2941.');

        if UserRec."User Name" <> UserId() then
            Error('User."User Name" is "%1" but UserId() is "%2"', UserRec."User Name", UserId());
        // The seed's row, not the fixture's: the two are told apart by Full Name.
        if UserRec."Full Name" = BackupUserTok then
            Error('the row reached by UserSecurityId() is the fixture''s stand-in, not the seed''s');
    end;
}
