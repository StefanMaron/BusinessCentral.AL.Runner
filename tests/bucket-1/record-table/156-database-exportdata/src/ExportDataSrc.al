/// Helper codeunit that wraps Database.ExportData so the test can
/// exercise it without calling it inline.
/// Actual signature: ExportData(showDialog: Boolean; var FileName: Text; ...)
codeunit 61600 "EDT Helper"
{
    /// Call Database.ExportData(showDialog, fileName) — must be a no-op stub
    /// in standalone mode (no external file I/O).
    procedure CallExportData(showDialog: Boolean)
    var
        FileName: Text;
    begin
        Database.ExportData(showDialog, FileName);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
