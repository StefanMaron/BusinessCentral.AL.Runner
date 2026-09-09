// Replaces the session user's row in User (2000000120) with a DIFFERENT user carrying the same
// name, from a DEPENDENCY install trigger -- i.e. before the consuming bundle's own install
// triggers run, and before the runner's session-user identity DECISION is made.
//
// This is the state a --test-data backup containing its own TESTUSER produces. It is written
// from AL because a fixture cannot restore a backup, and from a dependency because the ordering
// under test (AlRunner#3268) is precisely "the bundle's own install code versus the decision".
//
// DELETE-THEN-INSERT, not a bare insert (#3698). The seed's ROW is now written inside
// TestExecutor's dep-company baseline window, ahead of the dependency install triggers, so the
// session user is already in the table when this runs and BC refuses a second row under one
// name. Deleting first is what restoring a backup over the table does.
codeunit 70760 "ITSI Seed Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        UserRec: Record User;
        StandInSid: Guid;
    begin
        // The #3698 precondition, asserted: dependency install code finds the session user's own
        // row. Without the delete that follows, the insert below would be refused over the
        // duplicate name instead of arranging the collision this fixture is about.
        if not UserRec.Get(UserSecurityId()) then
            Error(
              'precondition: the session user %1 must already be a row in User (2000000120) when '
              + 'a DEPENDENCY install trigger runs (AlRunner#3698)', Format(UserSecurityId()));
        UserRec.Delete();

        Evaluate(StandInSid, StandInSidTok);
        UserRec.Init();
        UserRec."User Security ID" := StandInSid;
        // Same NAME as the runner's session user, different security id. BC refuses two users
        // under one name (SystemTableTriggers.OnBeforeInsertAsync, case 2000000120:), which the
        // runner reproduces, so the session-user seed cannot write its own row and adopts this
        // one instead (#2983).
        UserRec."User Name" := CopyStr(UserId(), 1, MaxStrLen(UserRec."User Name"));
        UserRec."Full Name" := StandInFullNameTok;
        UserRec.Insert();
    end;

    var
        StandInSidTok: Label '{D41F7A96-2C58-4E13-8B0A-7F5C9E62D3A4}', Locked = true;
        StandInFullNameTok: Label 'STANDS-IN-FOR-A-BACKUP-USER-3268', Locked = true;
}
