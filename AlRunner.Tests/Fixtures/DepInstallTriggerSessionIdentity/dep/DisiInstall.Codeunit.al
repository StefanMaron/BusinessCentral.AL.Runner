// A DEPENDENCY's OnInstallAppPerCompany, doing what ordinary dependency install code does:
// reading the session identity and looking the session user up in User (2000000120).
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
        Nobody: Guid;
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

        Observation.Insert();
    end;

    var
        ObservedCodeTok: Label 'OBSERVED', Locked = true;
        NobodyTok: Label '{DEADBEEF-1111-2222-3333-444455556666}', Locked = true;
}
