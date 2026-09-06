codeunit 70501 "SURA Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        InstalledByFixtureTok: Label 'INSTALLED-BY-FIXTURE', Locked = true;

    [Test]
    procedure SuraSeedLeftTheAlreadyPresentRowExactlyAsItWas()
    var
        UserRec: Record User;
    begin
        if not UserRec.Get(UserSecurityId()) then
            Error('the session user must still be a row in User after the seed ran');

        // The discriminator. If the seed had treated "already present" as something to repair
        // -- deleting and re-inserting, or modifying -- Full Name would carry the skeleton
        // NavUser's value instead of the fixture's marker.
        if UserRec."Full Name" <> InstalledByFixtureTok then
            Error(
              'User."Full Name" is "%1" but the row the Install trigger wrote said "%2" — the seed '
              + 'overwrote a row that was already correct', UserRec."Full Name", InstalledByFixtureTok);

        if UserRec."User Name" <> UserId() then
            Error('User."User Name" is "%1" but UserId() is "%2"', UserRec."User Name", UserId());
    end;

    [Test]
    procedure SuraSeedAddedNoSecondRowForTheSessionUser()
    var
        UserRec: Record User;
    begin
        // A seed that re-inserted under a different key, or inserted without checking, would
        // leave two rows carrying the session user's name. Exactly one is the claim.
        UserRec.SetRange("User Name", UserId());
        if UserRec.Count() <> 1 then
            Error('expected exactly 1 User row named "%1" but found %2', UserId(), UserRec.Count());
    end;

    [Test]
    procedure SuraAUserSecurityIdBelongingToNobodyIsStillNotFound()
    var
        UserRec: Record User;
        Nobody: Guid;
    begin
        // Negative control on the reads above: Get must actually consult the key rather than
        // answering true for anything, which would make both tests above vacuous.
        Evaluate(Nobody, '{DEADBEEF-1111-2222-3333-444455556666}');
        if UserRec.Get(Nobody) then
            Error('User.Get on a security id belonging to nobody must be false');
    end;
}
