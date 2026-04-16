/// Helper codeunit that wraps Database.ExportData so the test can
/// exercise it without calling it inline.
codeunit 61600 "EDT Helper"
{
    /// Call Database.ExportData(fileName) — must be a no-op stub
    /// in standalone mode (no external file I/O).
    procedure CallExportData(fileName: Text)
    begin
        Database.ExportData(fileName);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
