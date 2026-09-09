// Writes a User (2000000120) row carrying the SESSION user's name under a different security
// id, from a DEPENDENCY install trigger — i.e. before the runner's session-user seed runs, and
// before the consuming bundle's own install triggers run.
//
// This is the state a --test-data backup containing its own TESTUSER produces. It is written
// from AL because a fixture cannot restore a backup, and from a dependency because the ordering
// under test (AlRunner#3268) is precisely "the bundle's own install code versus the seed".
codeunit 70760 "ITSI Seed Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        UserRec: Record User;
        StandInSid: Guid;
    begin
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
