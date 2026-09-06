// Puts the session user's OWN row in User (2000000120) before the runner's seed runs.
//
// The ordering is the whole point and it is not incidental: TestExecutor runs a bundle's own
// Install triggers (install-seed-run-own-install-triggers) BEFORE
// RecordPatches.EnsureUserSystemTableRowSeeded (install-seed-user-row). So by the time the seed
// executes, the row it wants to write is already there and its ALInsert is refused by the
// PRIMARY KEY on "User Security ID".
//
// That refusal is the benign one. The row the seed exists to guarantee is present, so the run
// must carry on silently -- and it must not touch the row that is already there.
codeunit 70500 "SURA Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        UserRec: Record User;
    begin
        UserRec.Init();
        UserRec."User Security ID" := UserSecurityId();
        UserRec."User Name" := CopyStr(UserId(), 1, MaxStrLen(UserRec."User Name"));
        // The marker is how the tests tell "the seed left this row alone" from "the seed
        // replaced it": the runner's own seed writes the skeleton NavUser's Full Name, never
        // this string.
        UserRec."Full Name" := InstalledByFixtureTok;
        UserRec.Insert();
    end;

    var
        InstalledByFixtureTok: Label 'INSTALLED-BY-FIXTURE', Locked = true;
}
