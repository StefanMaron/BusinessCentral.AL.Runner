codeunit 70782 "ITSI Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        // The security id the DEPENDENCY's install trigger put on the stand-in row, and
        // therefore the id the session adopts.
        AdoptedSidTok: Label '{D41F7A96-2C58-4E13-8B0A-7F5C9E62D3A4}', Locked = true;
        // What BcRuntime generates for the skeleton session when nothing is adopted. Asserted
        // NOT to be the answer here, so an implementation that never adopts fails.
        GeneratedSidTok: Label '{C0A1BDFA-0000-0000-0000-545553545553}', Locked = true;
        StandInFullNameTok: Label 'STANDS-IN-FOR-A-BACKUP-USER-3268', Locked = true;
        OwnerCodeTok: Label 'OWNER', Locked = true;

    [Test]
    procedure ItsiTheDependencySeededTheSameNamedUser()
    var
        UserRec: Record User;
        AdoptedSid: Guid;
    begin
        // PRECONDITION. Without it every assertion below would also pass on a run where the
        // dependency's install trigger wrote nothing (or where a dep-company cache HIT restored
        // a baseline that had lost the row), and would then prove nothing about adoption at all.
        Evaluate(AdoptedSid, AdoptedSidTok);
        if not UserRec.Get(AdoptedSid) then
            Error('the dependency''s stand-in user %1 must be a row in User (2000000120)', AdoptedSidTok);
        if UserRec."User Name" <> UserId() then
            Error(
              'the stand-in user must carry the session user''s own name: expected "%1", got "%2"',
              UserId(), UserRec."User Name");
        if UserRec."Full Name" <> StandInFullNameTok then
            Error('User."Full Name" is "%1", expected "%2"', UserRec."Full Name", StandInFullNameTok);
    end;

    [Test]
    procedure ItsiTheSessionAdoptedTheDependencysSecurityId()
    var
        AdoptedSid: Guid;
        GeneratedSid: Guid;
    begin
        // The adoption itself still happens — this fixture is about WHEN it is decided, not
        // about whether it happens at all. Both concrete ids are asserted, so neither "never
        // adopts" nor "returns a constant" can satisfy this codeunit.
        Evaluate(AdoptedSid, AdoptedSidTok);
        Evaluate(GeneratedSid, GeneratedSidTok);
        if UserSecurityId() <> AdoptedSid then
            Error('UserSecurityId() must be the ADOPTED id %1, but it is %2',
              AdoptedSidTok, Format(UserSecurityId()));
        if UserSecurityId() = GeneratedSid then
            Error('UserSecurityId() is still the runner-generated id %1, so no adoption happened',
              GeneratedSidTok);
    end;

    [Test]
    procedure ItsiInstallCodeSawTheIdentityTheTestsSee()
    var
        Setup: Record "ITSI Setup";
    begin
        // THE #3268 DISCRIMINATOR. The bundle's install trigger stored UserSecurityId() while it
        // ran; the session-user seed decides adoption. If that decision is made AFTER the
        // trigger, the stored id is the runner-generated one and the session has since become a
        // different user — so the row install code keyed on names a user this session is not.
        if not Setup.Get(OwnerCodeTok) then
            Error('the bundle''s install trigger must have written the "%1" setup row', OwnerCodeTok);
        if Setup."Owner Security ID" <> UserSecurityId() then
            Error(
              'install code stored %1 as the owner, but UserSecurityId() is now %2 — the session '
              + 'user changed after the install triggers ran (AlRunner#3268)',
              Format(Setup."Owner Security ID"), Format(UserSecurityId()));
    end;

    [Test]
    procedure ItsiTheStoredOwnerIsTheAdoptedIdNotTheGeneratedOne()
    var
        Setup: Record "ITSI Setup";
        AdoptedSid: Guid;
        GeneratedSid: Guid;
    begin
        // Says WHICH way a failure went, and rules out the degenerate agreement where adoption
        // simply stopped happening: the test above would then pass with both values equal to the
        // generated id, while the fixture measured nothing.
        Evaluate(AdoptedSid, AdoptedSidTok);
        Evaluate(GeneratedSid, GeneratedSidTok);
        Setup.Get(OwnerCodeTok);
        if Setup."Owner Security ID" = GeneratedSid then
            Error(
              'install code stored the runner-GENERATED id %1: the adoption decision was made '
              + 'after this bundle''s install triggers ran', GeneratedSidTok);
        if Setup."Owner Security ID" <> AdoptedSid then
            Error('install code stored %1, expected the adopted id %2',
              Format(Setup."Owner Security ID"), AdoptedSidTok);
    end;

    [Test]
    procedure ItsiTheStoredOwnerNameIsUnchangedByAdoption()
    var
        Setup: Record "ITSI Setup";
    begin
        // Only the security id is adopted. UserId() is the key the adoption matched ON, so a
        // value stored during install must still agree with it — an implementation that moved
        // the whole identity onto the stand-in row would pass the id assertions and fail here.
        Setup.Get(OwnerCodeTok);
        if Setup."Owner User Name" <> UserId() then
            Error('install code stored the user name "%1", but UserId() is now "%2"',
              Setup."Owner User Name", UserId());
    end;
}
