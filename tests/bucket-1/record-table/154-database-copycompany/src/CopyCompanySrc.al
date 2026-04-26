/// Helper codeunit that wraps Database.CopyCompany so the test can
/// exercise it without calling it inline.
codeunit 61400 "CCP Helper"
{
    /// Call Database.CopyCompany(sourceCompany, destinationCompany) — must be a no-op stub
    /// in standalone mode (no multi-company data store).
    procedure CallCopyCompany(sourceCompany: Text; destinationCompany: Text)
    begin
        Database.CopyCompany(sourceCompany, destinationCompany);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
