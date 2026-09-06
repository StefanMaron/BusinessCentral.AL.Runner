// Puts a DIFFERENT user, carrying the session user's NAME, into User (2000000120) before the
// runner's seed runs.
//
// TestExecutor runs a bundle's own Install triggers before
// RecordPatches.EnsureUserSystemTableRowSeeded, so this row is in place when the seed executes.
// This is the shape a --test-data backup containing its own TESTUSER produces.
//
// WHAT REFUSES A DUPLICATE USER NAME, AND WHERE
//   On a real tier it is BC's system-table TRIGGER, not an index. Ncl's
//   SystemTableTriggers.OnBeforeInsertAsync has a `case 2000000120:` arm that validates a unique
//   user name -- along with the Windows SID, authentication email and application id -- before
//   the row is written. AlRunner/Patches/UserTableTriggerPatches.cs's own header records that
//   the runner reproduces exactly one thing from that arm, its User Property (2000000121)
//   companion insert, and states in as many words that "None of that [validation] is reproduced
//   here".
//
//   So the runner does not refuse this collision at all: the store behind the User table is
//   BC's own CreateTempDataAccess, which enforces the primary key and nothing else. MEASURED --
//   SurcTheSessionUserStillGetsItsOwnRow next door observes the seed landing anyway, and the run
//   is left holding two rows that share a user name where BC would hold one. That divergence is
//   AlRunner#2983.
//
// WHAT THIS FIXTURE IS FOR
//   It measures the hazard the #2941 review predicted, end to end, and shows the fix survives
//   it. It is also the canary for #2983: whichever way that gets closed -- reproducing the
//   trigger's validation, or enforcing uniqueness in the store -- the seed starts being refused
//   here, SurcTheSessionUserStillGetsItsOwnRow fails, and RecordPatches' Refused branch becomes
//   reachable from AL for the first time.
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
        // Same NAME as the runner's session user, different security id. This is the collision
        // BC's OnBeforeInsertAsync case 2000000120: arm would refuse and the runner does not.
        UserRec."User Name" := CopyStr(UserId(), 1, MaxStrLen(UserRec."User Name"));
        UserRec."Full Name" := BackupUserTok;
        UserRec.Insert();
    end;

    var
        CollidingSidTok: Label '{A17E9C42-5B08-4D6F-9E31-0C7A2F84B155}', Locked = true;
        BackupUserTok: Label 'STANDS-IN-FOR-A-BACKUP-USER', Locked = true;
}
