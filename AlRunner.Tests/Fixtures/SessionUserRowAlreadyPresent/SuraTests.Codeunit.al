codeunit 70501 "SURA Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        InstalledByFixtureTok: Label 'INSTALLED-BY-FIXTURE', Locked = true;
        // What BcRuntime generates for the skeleton session. Nothing here is adopted, so this
        // is what UserSecurityId() must answer.
        GeneratedSidTok: Label '{C0A1BDFA-0000-0000-0000-545553545553}', Locked = true;

    [Test]
    procedure SuraUserSecurityIdIsTheRunnerGeneratedOneWhenNothingIsAdopted()
    var
        GeneratedSid: Guid;
    begin
        // THE NEGATIVE DIRECTION for #2983's adoption, and it is why an implementation that
        // simply returned some existing row's id cannot pass both fixtures.
        //
        // Here the row already in User carries the session user's OWN security id, so the seed's
        // insert is refused by the PRIMARY KEY -- the benign AlreadyPresent path -- and there is
        // nothing to adopt. UserSecurityId() must therefore still be the value BcRuntime
        // generated, asserted as a concrete constant rather than as "not empty".
        //
        // The sibling fixture SessionUserRowNameCollision asserts the OTHER concrete value,
        // {A17E9C42-5B08-4D6F-9E31-0C7A2F84B155}, adopted from the data. One implementation
        // cannot satisfy both by returning a constant.
        Evaluate(GeneratedSid, GeneratedSidTok);
        if UserSecurityId() <> GeneratedSid then
            Error(
              'with no row to adopt, UserSecurityId() must be the runner-generated %1, but it is %2',
              GeneratedSidTok, Format(UserSecurityId()));
    end;

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
