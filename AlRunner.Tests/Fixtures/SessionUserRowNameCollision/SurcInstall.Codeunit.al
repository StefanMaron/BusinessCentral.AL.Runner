// Puts a DIFFERENT user, carrying the session user's NAME, into User (2000000120) before the
// runner's seed runs.
//
// TestExecutor runs a bundle's own Install triggers before
// RecordPatches.EnsureUserSystemTableRowSeeded, so this row is in place when the seed executes.
// BC's User table has a UNIQUE key on "User Name" as well as its primary key on
// "User Security ID", so the seed's insert is refused -- and, unlike the primary-key refusal
// next door in SessionUserRowAlreadyPresent, it leaves NO row for the session user's own
// security id.
//
// This is the shape a --test-data backup containing its own TESTUSER produces, and it is what
// silently defeated the entire #2296 fix: the seed used DataError.TrapError, which converts the
// refusal into a `false` return rather than an exception, discarded that bool, logged nothing,
// and set _userRowSeededForThisBundle = true regardless.
codeunit 70520 "SURC Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        UserRec: Record User;
        CollidingSid: Guid;
    begin
        Evaluate(CollidingSid, CollidingSidTok);
        UserRec.Init();
        UserRec."User Security ID" := CollidingSid;
        // Same NAME as the runner's session user, different security id -- the unique key on
        // "User Name" is what the seed then collides with.
        UserRec."User Name" := CopyStr(UserId(), 1, MaxStrLen(UserRec."User Name"));
        UserRec."Full Name" := BackupUserTok;
        UserRec.Insert();
    end;

    var
        CollidingSidTok: Label '{A17E9C42-5B08-4D6F-9E31-0C7A2F84B155}', Locked = true;
        BackupUserTok: Label 'STANDS-IN-FOR-A-BACKUP-USER', Locked = true;
}
