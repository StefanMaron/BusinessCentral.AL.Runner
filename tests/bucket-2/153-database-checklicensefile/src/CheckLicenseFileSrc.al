/// Helper codeunit that wraps Database.CheckLicenseFile so the test can
/// exercise it without calling it inline.
codeunit 61200 "CLF Helper"
{
    /// Call Database.CheckLicenseFile() — must be a no-op stub in standalone mode.
    procedure CallCheckLicenseFile()
    begin
        Database.CheckLicenseFile();
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
