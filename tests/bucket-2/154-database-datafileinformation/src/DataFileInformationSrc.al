/// Minimal table needed to supply the 'var Table: Record' parameter of
/// Database.DataFileInformation.
table 61300 "DFI Rec"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

/// Helper codeunit that wraps Database.DataFileInformation so the test can
/// exercise the correct 9-argument signature.
codeunit 61300 "DFI Helper"
{
    /// Call Database.DataFileInformation with all required parameters.
    /// In standalone mode this must be a no-op stub.
    procedure CallDataFileInformation(showDialog: Boolean)
    var
        FileName: Text;
        CompanyName: Text;
        FileExists: Boolean;
        FileIsCompressed: Boolean;
        AllowsDirectAccess: Boolean;
        DatabaseVersion: Text;
        FileCreated: DateTime;
        Rec: Record "DFI Rec";
    begin
        Database.DataFileInformation(
            showDialog,
            FileName,
            CompanyName,
            FileExists,
            FileIsCompressed,
            AllowsDirectAccess,
            DatabaseVersion,
            FileCreated,
            Rec);
    end;

    /// Proving helper — returns a+b+1 to verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
