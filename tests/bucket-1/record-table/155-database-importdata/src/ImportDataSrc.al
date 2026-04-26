/// Helper codeunit that wraps Database.ImportData so the test can
/// exercise it without calling it inline.
codeunit 61500 "IDT Helper"
{
    /// Call Database.ImportData(showDialog, path, create) — must be a no-op stub
    /// in standalone mode (no external file I/O).
    procedure CallImportData(showDialog: Boolean)
    var
        Path: Text;
        Create: Boolean;
    begin
        Database.ImportData(showDialog, Path, Create);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
