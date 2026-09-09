// Puts the fixture's own marker on the session user's row in User (2000000120), before the
// runner's seed decides the session identity.
//
// THE ORDERING, AND WHY IT CHANGED (#3698). The seed's ROW is now written inside TestExecutor's
// dep-company baseline window, ahead of the DEPENDENCY install triggers, so that dependency
// install code finds the session user in the table. A dependency can therefore no longer INSERT
// that row - the seed already did, and BC refuses a second user under one name or one security
// id, which the runner reproduces. So this codeunit MODIFIES the row instead, which is the state
// a backup restored over the seeded table leaves behind.
//
// What the fixture measures is unchanged: the seed's second call - the identity DECISION, made
// after the window on every path - finds a row for the session user's own security id, must take
// the benign already-present path, and must not touch the row that is there.
codeunit 70500 "SURA Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        UserRec: Record User;
    begin
        // Asserted rather than tested: without this row the fixture measures nothing, and a
        // silent miss would turn every test in the bundle into a vacuous pass. It is also the
        // #3698 precondition - dependency install code sees the session user's row.
        if not UserRec.Get(UserSecurityId()) then
            Error(
              'precondition: the session user %1 must already be a row in User (2000000120) when '
              + 'a DEPENDENCY install trigger runs (AlRunner#3698)', Format(UserSecurityId()));

        // The marker is how the tests tell "the seed left this row alone" from "the seed
        // replaced it": the runner's own seed writes the skeleton NavUser's Full Name, never
        // this string.
        UserRec."Full Name" := InstalledByFixtureTok;
        UserRec.Modify();
    end;

    var
        InstalledByFixtureTok: Label 'INSTALLED-BY-FIXTURE', Locked = true;
}
