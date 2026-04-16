/// Helper codeunit that wraps Database.DataFileInformation so the test can
/// exercise it without calling it inline.
codeunit 61300 "DFI Helper"
{
    /// Call Database.DataFileInformation(showDialog) — must be a no-op stub in standalone mode.
    procedure CallDataFileInformation(showDialog: Boolean)
    begin
        Database.DataFileInformation(showDialog);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
