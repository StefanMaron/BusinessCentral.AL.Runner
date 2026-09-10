codeunit 70842 "BISV Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        ObservedCodeTok: Label 'OBSERVED', Locked = true;
        SuperTok: Label 'SUPER', Locked = true;
        OwnAppIdTok: Label '{5d2c7f16-84ab-4e39-b0d5-9c17ae4f2b83}', Locked = true;

    [Test]
    procedure BisvTheInstallTriggerRecordedWhatItSaw()
    var
        Observation: Record "BISV Observation";
    begin
        // PRECONDITION. Without it every assertion below would also pass on a run where this
        // bundle's install trigger never executed, and would prove nothing at all.
        if not Observation.Get(ObservedCodeTok) then
            Error('this bundle''s install trigger must have written the "%1" observation row',
              ObservedCodeTok);
        if Observation."Observed Security ID" <> UserSecurityId() then
            Error('install code saw the session identity %1, but UserSecurityId() is now %2',
              Format(Observation."Observed Security ID"), Format(UserSecurityId()));
    end;

    [Test]
    procedure BisvInstallCodeSawTheCompanyRow()
    var
        Observation: Record "BISV Observation";
    begin
        // THE #3757 DISCRIMINATOR for Company (2000000006): the seed ran after this trigger, so
        // Company.Get(CompanyName()) answered false inside install code for the one company
        // every other surface reports as existing.
        Observation.Get(ObservedCodeTok);
        if not Observation."Company Row Existed" then
            Error('install code called Company.Get(CompanyName()) and found no row (AlRunner#3757)');
        if Observation."Company Row Name" <> CompanyName() then
            Error('the Company row install code found was named "%1", expected "%2"',
              Observation."Company Row Name", CompanyName());
        // NEGATIVE CONTROL, recorded inside the same trigger.
        if Observation."Other Company Row Existed" then
            Error('Company.Get on a company that does not exist must be false, even during install');
    end;

    [Test]
    procedure BisvInstallCodeSawTheSuperGrant()
    var
        Observation: Record "BISV Observation";
        AccessCtrl: Record "Access Control";
    begin
        // THE #3757 DISCRIMINATOR for Access Control (2000000053).
        Observation.Get(ObservedCodeTok);
        if not Observation."Super Row Existed" then
            Error('install code found no SUPER row in Access Control (2000000053) for UserSecurityId() (AlRunner#3757)');
        if Observation."Nobody Super Row Existed" then
            Error('a SUPER row must not exist for a user security id belonging to nobody');
        AccessCtrl.SetRange("User Security ID", UserSecurityId());
        AccessCtrl.SetRange("Role ID", SuperTok);
        if AccessCtrl.Count() <> 1 then
            Error('expected exactly 1 SUPER row for UserSecurityId() at test time but found %1',
              AccessCtrl.Count());
    end;

    [Test]
    procedure BisvInstallCodeSawItsOwnPublishedApplicationRow()
    var
        Observation: Record "BISV Observation";
        PublishedApp: Record "Published Application";
        OwnAppId: Guid;
    begin
        // THE #3757 DISCRIMINATOR for the bundle's own Published Application row (2000000206),
        // seeded after this trigger: an app's install code asking whether its own module is
        // published - the shape System Application's module-ownership checks use - found
        // nothing.
        Observation.Get(ObservedCodeTok);
        if not Observation."Own Published App Row Existed" then
            Error('install code found no Published Application row for its own app id (AlRunner#3757)');
        if Observation."Other Published App Row Existed" then
            Error('a Published Application row must not exist for an app id nothing published');
        if not Observation."Own App Installed Row Existed" then
            Error('install code found no NAV App Installed App row for its own app id (AlRunner#3757)');
        // Exactly one row at test time - a seed that ran twice would leave two.
        Evaluate(OwnAppId, OwnAppIdTok);
        PublishedApp.SetRange(ID, OwnAppId);
        if PublishedApp.Count() <> 1 then
            Error('expected exactly 1 Published Application row for this bundle but found %1',
              PublishedApp.Count());
    end;
}
