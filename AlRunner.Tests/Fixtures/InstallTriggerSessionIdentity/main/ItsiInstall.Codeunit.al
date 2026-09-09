// The bundle's OWN install trigger. It stores the session identity it observes, and nothing
// else — the row the seed adopts is written by the sibling dependency app, so this trigger's
// only job is to witness what UserSecurityId() answered while install code was running.
codeunit 70781 "ITSI Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        Setup: Record "ITSI Setup";
    begin
        Setup.Init();
        Setup."Code" := OwnerCodeTok;
        Setup."Owner Security ID" := UserSecurityId();
        Setup."Owner User Name" := CopyStr(UserId(), 1, MaxStrLen(Setup."Owner User Name"));
        Setup.Insert();
    end;

    var
        OwnerCodeTok: Label 'OWNER', Locked = true;
}
