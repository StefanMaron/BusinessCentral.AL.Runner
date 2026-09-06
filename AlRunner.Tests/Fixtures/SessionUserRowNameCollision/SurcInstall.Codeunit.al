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
//   UserTableTriggerPatches REPRODUCES that arm now (#2983), so the runner refuses this
//   collision the way BC does. Until it did, the store behind the User table -- BC's own
//   CreateTempDataAccess -- enforced the primary key and nothing else, the seed landed anyway,
//   and the run was left holding two rows that share a user name where BC would hold one.
//
// WHAT THIS FIXTURE IS FOR
//   It measures the hazard the #2941 review predicted, end to end. It was the canary for #2983,
//   and the canary fired: SurcTheSessionUserStillGetsItsOwnRow -- which asserted the seed
//   landing -- was replaced by SurcTheSessionUserIsRefusedItsOwnRowOverTheDuplicateName, and
//   RecordPatches.EnsureUserSystemTableRowSeeded's Refused branch is now reachable from AL for
//   the first time. What the fixture pins from here on is that the refusal is CLEAN: one row
//   under the name, the stand-in's, and a seed that reports what it did rather than claiming
//   the row is there.
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
