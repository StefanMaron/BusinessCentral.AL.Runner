/// Helper codeunit that wraps Database.CheckLicenseFile(KeyNumber) so the
/// test can exercise it without calling it inline.
codeunit 61220 "CLF Helper"
{
    /// Call Database.CheckLicenseFile(keyNumber) — must be a no-op stub in standalone mode.
    procedure CallCheckLicenseFile(keyNumber: Integer)
    begin
        Database.CheckLicenseFile(keyNumber);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
