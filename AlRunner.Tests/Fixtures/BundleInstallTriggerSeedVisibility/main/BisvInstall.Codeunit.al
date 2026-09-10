// The BUNDLE's own OnInstallAppPerCompany, reading the three system tables AlRunner#3757 is
// about. It only OBSERVES - it seeds nothing and changes none of them - so what it stores is
// what any app's install code would have seen at that moment.
codeunit 70841 "BISV Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        Observation: Record "BISV Observation";
        Comp: Record Company;
        AccessCtrl: Record "Access Control";
        PublishedApp: Record "Published Application";
        InstalledApp: Record "NAV App Installed App";
        Nobody: Guid;
        OwnAppId: Guid;
        NoSuchAppId: Guid;
    begin
        Evaluate(Nobody, NobodyTok);
        Evaluate(OwnAppId, OwnAppIdTok);
        Evaluate(NoSuchAppId, NoSuchAppIdTok);

        Observation.Init();
        Observation."Code" := ObservedCodeTok;
        Observation."Observed Security ID" := UserSecurityId();

        Observation."Company Row Existed" := Comp.Get(CompanyName());
        if Observation."Company Row Existed" then
            Observation."Company Row Name" := Comp.Name;
        // Negative control, recorded inside the trigger: Get must consult the key here too.
        Observation."Other Company Row Existed" := Comp.Get(NoSuchCompanyTok);

        AccessCtrl.SetRange("User Security ID", UserSecurityId());
        AccessCtrl.SetRange("Role ID", SuperTok);
        Observation."Super Row Existed" := not AccessCtrl.IsEmpty();
        AccessCtrl.SetRange("User Security ID", Nobody);
        Observation."Nobody Super Row Existed" := not AccessCtrl.IsEmpty();

        PublishedApp.SetRange(ID, OwnAppId);
        Observation."Own Published App Row Existed" := not PublishedApp.IsEmpty();
        PublishedApp.SetRange(ID, NoSuchAppId);
        Observation."Other Published App Row Existed" := not PublishedApp.IsEmpty();

        InstalledApp.SetRange("App ID", OwnAppId);
        Observation."Own App Installed Row Existed" := not InstalledApp.IsEmpty();

        Observation.Insert();
    end;

    var
        ObservedCodeTok: Label 'OBSERVED', Locked = true;
        NobodyTok: Label '{DEADBEEF-1111-2222-3333-444455556666}', Locked = true;
        NoSuchCompanyTok: Label 'NO SUCH COMPANY', Locked = true;
        SuperTok: Label 'SUPER', Locked = true;
        OwnAppIdTok: Label '{5d2c7f16-84ab-4e39-b0d5-9c17ae4f2b83}', Locked = true;
        NoSuchAppIdTok: Label '{0BADC0DE-1111-2222-3333-444455556666}', Locked = true;
}
