// Replaces the session user's row in User (2000000120) with a DIFFERENT user carrying the same
// name - the state a --test-data backup containing its own TESTUSER produces.
//
// THE ORDERING, AND WHY IT CHANGED (#3698). The seed's ROW is now written inside TestExecutor's
// dep-company baseline window, ahead of the DEPENDENCY install triggers, so a dependency cannot
// simply INSERT a same-named user any more: BC refuses a duplicate user name from a TRIGGER
// (SystemTableTriggers.OnBeforeInsertAsync's `case 2000000120:` arm calls
// IsUserFieldUniqueAsync(recordBuffer, 2, insert: true), and AlRunner/Patches/UserTableTriggerPatches.cs
// reproduces it, #2983), and the seeded row is already there. So this codeunit DELETES the
// seeded row first and inserts the stand-in in its place, which is what restoring a backup over
// the table does.
//
// WHAT THE SEED DOES ABOUT IT: ADOPT (maintainer decision, 2026-09-06)
//   The seed's second call - the identity DECISION, made after the window on every path - finds
//   no row for the session's own security id and one carrying its name, so it ADOPTS that row's
//   security id: UserSecurityId() answers {A17E9C42-5B08-4D6F-9E31-0C7A2F84B155} for the rest of
//   the run and no row is written. The alternative - refuse, and run as a user present in no row
//   - is the state AlRunner#2296 exists to remove, and it was this fixture that measured it.
//
// WHAT THIS FIXTURE IS FOR
//   The ADOPTION being complete and loud: the session resolves to THIS row, no second row is
//   written, UserId() is untouched, the adopted user keeps its User Property companion row, and
//   the seed says on stderr where the id came from.
codeunit 70520 "SURC Installer"
{
    Subtype = Install;

    trigger OnInstallAppPerCompany()
    var
        UserRec: Record User;
        UserProperty: Record "User Property";
        CollidingSid: Guid;
    begin
        // Asserted rather than tested: this is the #3698 precondition (dependency install code
        // finds the session user's row), and without the delete below the insert that follows
        // would be refused over the duplicate name instead of arranging the collision.
        if not UserRec.Get(UserSecurityId()) then
            Error(
              'precondition: the session user %1 must already be a row in User (2000000120) when '
              + 'a DEPENDENCY install trigger runs (AlRunner#3698)', Format(UserSecurityId()));
        UserRec.Delete();

        Evaluate(CollidingSid, CollidingSidTok);
        UserRec.Init();
        UserRec."User Security ID" := CollidingSid;
        // Same NAME as the runner's session user, different security id.
        UserRec."User Name" := CopyStr(UserId(), 1, MaxStrLen(UserRec."User Name"));
        UserRec."Full Name" := BackupUserTok;
        UserRec.Insert();

        // THEN TAKE THE COMPANION ROW BACK OFF, so the adoption path's own
        // EnsureUserPropertyRow call is the ONLY thing that can put it back.
        //
        // Why this is needed at all: AL cannot write a User row that bypasses the runner's
        // insert prepend, so the Insert above already created the User Property (2000000121)
        // companion. Leaving it there made SurcTheAdoptedUserHasItsUserPropertyRow pass whether
        // or not adoption re-establishes the invariant -- it pinned "adoption does not LOSE the
        // row" and nothing more. Deleting it is the state a backup restored without table
        // 2000000121 leaves behind, and it turns that test into a real RED -> GREEN: with the
        // EnsureUserPropertyRow call removed from TryAdoptSessionUserSecurityId, it fails.
        //
        // The Get is asserted rather than tested, because a silent miss here would quietly
        // restore the vacuous version of the test. It also pins the prepend running for an
        // Install-trigger insert, which is the premise the paragraph above rests on.
        if not UserProperty.Get(CollidingSid) then
            Error(
              'precondition: the User insert above must have created the User Property (2000000121) '
              + 'companion row for %1 -- without it this fixture cannot tell whether adoption '
              + 'CREATES that row or merely fails to lose it', CollidingSidTok);
        UserProperty.Delete();
    end;

    var
        CollidingSidTok: Label '{A17E9C42-5B08-4D6F-9E31-0C7A2F84B155}', Locked = true;
        BackupUserTok: Label 'STANDS-IN-FOR-A-BACKUP-USER', Locked = true;
}
