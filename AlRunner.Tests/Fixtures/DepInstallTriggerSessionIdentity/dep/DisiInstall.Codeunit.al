// A DEPENDENCY's OnInstallAppPerCompany, doing what ordinary dependency install code does:
// reading the session identity and looking the session user up in User (2000000120), and - since
// AlRunner#3757 - the Company row, the Access Control SUPER row, and the installed-app registry.
//
// It writes no User row and arranges no collision. That is deliberate - this fixture measures
// what a dependency install trigger OBSERVES, and a trigger that changed the User table would
// be measuring its own edit instead (AlRunner#3698).
codeunit 70801 "DISI Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        Observation: Record "DISI Observation";
        UserRec: Record User;
        Comp: Record Company;
        AccessCtrl: Record "Access Control";
        InstalledApp: Record "NAV App Installed App";
        Nobody: Guid;
        OwnAppId: Guid;
        BundleAppId: Guid;
    begin
        Observation.Init();
        Observation."Code" := ObservedCodeTok;
        Observation."Observed Security ID" := UserSecurityId();
        Observation."Observed User Name" := CopyStr(UserId(), 1, MaxStrLen(Observation."Observed User Name"));

        Observation."Session User Row Existed" := UserRec.Get(UserSecurityId());
        if Observation."Session User Row Existed" then
            Observation."Session User Row Name" := UserRec."User Name";

        // Negative control, recorded from INSIDE the install trigger: Get must consult the key
        // here too, or "Session User Row Existed" would be true for any implementation that
        // answered true for everything, and the assertion on it would prove nothing.
        Evaluate(Nobody, NobodyTok);
        Observation."Nobody Row Existed" := UserRec.Get(Nobody);

        // #3757 - the Company row (2000000006). On a service tier the company exists long before
        // any extension is installed, so install code that resolves CompanyName() against it
        // finds a row.
        Observation."Company Row Existed" := Comp.Get(CompanyName());
        if Observation."Company Row Existed" then
            Observation."Company Row Name" := Comp.Name;
        // Negative control for the line above: Get must consult the key.
        Observation."Other Company Row Existed" := Comp.Get(NoSuchCompanyTok);

        // #3757 - the Access Control SUPER row (2000000053) that backs IsSuper for the session
        // user. Read as a table rather than through Codeunit "User Permissions": that codeunit
        // is System Application, which this fixture deliberately does not depend on.
        AccessCtrl.SetRange("User Security ID", UserSecurityId());
        AccessCtrl.SetRange("Role ID", SuperTok);
        Observation."Super Row Existed" := not AccessCtrl.IsEmpty();
        // Negative control: the same query for a user that does not exist must find nothing.
        AccessCtrl.SetRange("User Security ID", Nobody);
        Observation."Nobody Super Row Existed" := not AccessCtrl.IsEmpty();

        // #3757 - the installed-app registry (2000000153). POSITIVE control: this app's own row
        // is seeded before the dependency install triggers and must be there.
        Evaluate(OwnAppId, OwnAppIdTok);
        InstalledApp.SetRange("App ID", OwnAppId);
        Observation."Own App Installed Row Existed" := not InstalledApp.IsEmpty();
        // ...and the BUNDLE's row must NOT be, which is the runner's dependency/bundle split and
        // BC's own publish order: the app under test is not installed while its dependency's
        // install code runs. See docs/session-user-seed-ordering.md.
        Evaluate(BundleAppId, BundleAppIdTok);
        InstalledApp.SetRange("App ID", BundleAppId);
        Observation."Bundle App Installed Row Existed" := not InstalledApp.IsEmpty();

        Observation.Insert();
    end;

    var
        ObservedCodeTok: Label 'OBSERVED', Locked = true;
        NobodyTok: Label '{DEADBEEF-1111-2222-3333-444455556666}', Locked = true;
        NoSuchCompanyTok: Label 'NO SUCH COMPANY', Locked = true;
        SuperTok: Label 'SUPER', Locked = true;
        OwnAppIdTok: Label '{b7f41d92-3c65-4a08-9d17-52e6c8a3b410}', Locked = true;
        BundleAppIdTok: Label '{3a08f5c7-91be-4d26-8f43-6c5b71ea2d94}', Locked = true;
}
