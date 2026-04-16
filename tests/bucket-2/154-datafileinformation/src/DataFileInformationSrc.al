/// Helper codeunit that wraps Database.DataFileInformation() so the test
/// codeunit can call it without a direct Database reference that might
/// confuse the compiler in isolated contexts.
codeunit 61400 "DFI Helper"
{
    procedure GetDataFileInformation(): Text
    begin
        exit(Database.DataFileInformation());
    end;
}
