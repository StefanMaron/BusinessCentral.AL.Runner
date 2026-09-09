codeunit 70820 "DISI Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        ObservedCodeTok: Label 'OBSERVED', Locked = true;
        // What BcRuntime generates for the skeleton session. Nothing here is adopted, so this is
        // what UserSecurityId() must answer - asserted as a concrete constant rather than as
        // "not empty", so an implementation returning a default cannot pass.
        GeneratedSidTok: Label '{C0A1BDFA-0000-0000-0000-545553545553}', Locked = true;

    [Test]
    procedure DisiTheDependencyRecordedWhatItSaw()
    var
        Observation: Record "DISI Observation";
    begin
        // PRECONDITION. Without it every assertion below would also pass on a run where the
        // dependency's install trigger never executed, and would prove nothing at all.
        if not Observation.Get(ObservedCodeTok) then
            Error('the dependency''s install trigger must have written the "%1" observation row',
              ObservedCodeTok);
        if Observation."Observed User Name" <> UserId() then
            Error('the dependency saw UserId() = "%1", but UserId() is now "%2"',
              Observation."Observed User Name", UserId());
    end;

    [Test]
    procedure DisiDependencyInstallCodeSawTheSessionUsersOwnRow()
    var
        Observation: Record "DISI Observation";
    begin
        // THE #3698 DISCRIMINATOR. The session-user seed used to run AFTER the dep-company
        // baseline window, so a DEPENDENCY install trigger looked the session user up in an
        // empty User table: every TableRelation to User."User Security ID" it wrote refused the
        // id UserSecurityId() itself returned, and the identity could still be moved onto an
        // adopted row afterwards.
        Observation.Get(ObservedCodeTok);
        if not Observation."Session User Row Existed" then
            Error(
              'dependency install code looked up UserSecurityId() in User (2000000120) and found '
              + 'no row: the session-user seed had not run yet (AlRunner#3698)');
        if Observation."Session User Row Name" <> UserId() then
            Error(
              'the row the dependency found carried the user name "%1", expected "%2"',
              Observation."Session User Row Name", UserId());
    end;

    [Test]
    procedure DisiTheDependencySawTheIdentityTheTestsSee()
    var
        Observation: Record "DISI Observation";
        GeneratedSid: Guid;
    begin
        // The identity is SETTLED by the time dependency install code runs: what it observed is
        // what the tests observe. Both sides are asserted against the concrete generated id as
        // well as against each other, so "they agree because nothing was ever set" cannot pass.
        Evaluate(GeneratedSid, GeneratedSidTok);
        Observation.Get(ObservedCodeTok);
        if Observation."Observed Security ID" <> UserSecurityId() then
            Error(
              'dependency install code stored %1 as the session identity, but UserSecurityId() is '
              + 'now %2 - the session user changed after the dependency install triggers ran '
              + '(AlRunner#3698)',
              Format(Observation."Observed Security ID"), Format(UserSecurityId()));
        if UserSecurityId() <> GeneratedSid then
            Error('with no row to adopt, UserSecurityId() must be the runner-generated %1, but it is %2',
              GeneratedSidTok, Format(UserSecurityId()));
    end;

    [Test]
    procedure DisiTheDependencysLookupConsultedTheKey()
    var
        Observation: Record "DISI Observation";
    begin
        // NEGATIVE CONTROL for the test above, recorded from inside the same install trigger: a
        // Get answering true for anything would make "Session User Row Existed" meaningless.
        Observation.Get(ObservedCodeTok);
        if Observation."Nobody Row Existed" then
            Error('User.Get on a security id belonging to nobody must be false, even during install');
    end;

    [Test]
    procedure DisiTheSeedWroteExactlyOneRowForTheSessionUser()
    var
        UserRec: Record User;
    begin
        // Seeding earlier must not seed TWICE. The seed is called at two points now - inside the
        // dep-company window and again after it - and a second insert would leave two rows under
        // one user name, a state real BC refuses to hold.
        if not UserRec.Get(UserSecurityId()) then
            Error('the session user must be a row in User (2000000120) at test time');
        UserRec.SetRange("User Name", UserId());
        if UserRec.Count() <> 1 then
            Error('expected exactly 1 User row named "%1" but found %2', UserId(), UserRec.Count());
    end;
}
