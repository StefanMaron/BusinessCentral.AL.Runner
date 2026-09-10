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
        SuperTok: Label 'SUPER', Locked = true;

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

    [Test]
    procedure DisiDependencyInstallCodeSawTheCompanyRow()
    var
        Observation: Record "DISI Observation";
    begin
        // THE #3757 DISCRIMINATOR for the Company system table (2000000006). The Company seed
        // used to run after BOTH sets of install triggers, so a dependency's install code
        // resolved CompanyName() against a table with no row for the company it was
        // initialising - Company.Get(CompanyName()) answered false for the one company every
        // other surface reports as existing.
        Observation.Get(ObservedCodeTok);
        if not Observation."Company Row Existed" then
            Error(
              'dependency install code called Company.Get(CompanyName()) and found no row: the '
              + 'Company seed had not run yet (AlRunner#3757)');
        if Observation."Company Row Name" <> CompanyName() then
            Error('the Company row the dependency found was named "%1", expected "%2"',
              Observation."Company Row Name", CompanyName());
    end;

    [Test]
    procedure DisiTheDependencysCompanyLookupConsultedTheKey()
    var
        Observation: Record "DISI Observation";
    begin
        // NEGATIVE CONTROL for the test above, recorded inside the same install trigger: a Get
        // answering true for anything would make "Company Row Existed" meaningless.
        Observation.Get(ObservedCodeTok);
        if Observation."Other Company Row Existed" then
            Error('Company.Get on a company that does not exist must be false, even during install');
    end;

    [Test]
    procedure DisiDependencyInstallCodeSawTheSuperGrant()
    var
        Observation: Record "DISI Observation";
        AccessCtrl: Record "Access Control";
    begin
        // THE #3757 DISCRIMINATOR for Access Control (2000000053). The SUPER row was seeded
        // after both sets of install triggers, so install code reading the table found no
        // assignment for a session user the runner reports as SUPER everywhere else.
        Observation.Get(ObservedCodeTok);
        if not Observation."Super Row Existed" then
            Error(
              'dependency install code found no SUPER row in Access Control (2000000053) for '
              + 'UserSecurityId(): the Access Control seed had not run yet (AlRunner#3757)');
        if Observation."Nobody Super Row Existed" then
            Error('a SUPER row must not exist for a user security id belonging to nobody');
        // ...and the row install code saw is still there at test time, for the SAME id: the
        // assertion above would also pass on an implementation that seeded a row for one id and
        // then moved the session onto another.
        AccessCtrl.SetRange("User Security ID", UserSecurityId());
        AccessCtrl.SetRange("Role ID", SuperTok);
        if AccessCtrl.IsEmpty() then
            Error('Access Control must still hold a SUPER row for UserSecurityId() at test time');
    end;

    [Test]
    procedure DisiTheDependencySawItsOwnAppInstalledAndNotTheBundle()
    var
        Observation: Record "DISI Observation";
    begin
        // The registry half. POSITIVE: the dependency apps' rows are seeded before their install
        // triggers (#2963), so this app's own row is there - without which the negative below
        // would pass over an empty table and prove nothing.
        Observation.Get(ObservedCodeTok);
        if not Observation."Own App Installed Row Existed" then
            Error('the dependency''s own row in NAV App Installed App (2000000153) must exist while its install code runs');
        // NEGATIVE: the bundle under test is NOT installed yet while its dependency installs -
        // the runner seeds the bundle's own row after this window, and a service tier installs
        // a dependency before the app that depends on it.
        if Observation."Bundle App Installed Row Existed" then
            Error('the bundle under test must NOT be in NAV App Installed App while its DEPENDENCY''s install code runs');
    end;
}
