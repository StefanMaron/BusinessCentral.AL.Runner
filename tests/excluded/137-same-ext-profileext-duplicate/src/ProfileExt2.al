// Second profileextension named "DuplicatedProfileExt" — same extension as ProfileExt1.
// This must cause an AL0197 error that is NOT suppressed because both
// objects come from the same extension identity.
profileextension DuplicatedProfileExt extends BLANK
{
    Enabled = false;
}
