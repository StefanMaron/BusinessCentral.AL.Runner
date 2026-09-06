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
//   the row is written. AlRunner/Patches/UserTableTriggerPatches.cs reproduces that arm (#2983),
//   so the runner refuses this collision the way BC does. Until it did, the store behind the
//   User table -- BC's own CreateTempDataAccess -- enforced the primary key and nothing else,
//   the seed landed anyway, and the run was left holding two rows that share a user name where
//   BC would hold one.
//
// WHAT THE SEED DOES ABOUT IT: ADOPT (maintainer decision, 2026-09-06)
//   BC's refusal is right about the ROW, and it leaves open what the SESSION should be. The
//   runner ADOPTS: it takes this stand-in row's security id as the session's own, so
//   UserSecurityId() answers {A17E9C42-5B08-4D6F-9E31-0C7A2F84B155} for the rest of the run and
//   no row is written. The alternative -- refuse, and run as a user present in no row -- is the
//   state AlRunner#2296 exists to remove, and it was this fixture that measured it.
//
// WHAT THIS FIXTURE IS FOR
//   It measures the hazard the #2941 review predicted, end to end, and it has now flipped twice.
//   SurcTheSessionUserStillGetsItsOwnRow (the seed landing) became
//   SurcTheSessionUserIsRefusedItsOwnRowOverTheDuplicateName when #2983 added the uniqueness
//   arm, and that became SurcTheSessionAdoptedTheExistingRowsSecurityId when the maintainer
//   chose adoption. What it pins from here on is the ADOPTION being complete and loud: the
//   session resolves to THIS row, no second row is written, UserId() is untouched, the adopted
//   user keeps its User Property companion row, and the seed says on stderr where the id came
//   from.
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
