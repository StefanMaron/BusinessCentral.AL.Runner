/// Minimal table so we can open a RecordRef and extract a KeyRef.
table 61000 "DAK Item"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Description; Text[100]) { }
    }
    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

/// Helper codeunit that wraps Database.AlterKey(KeyRef, Boolean).
codeunit 61000 "DAK Helper"
{
    /// Call Database.AlterKey(primaryKeyRef, clustered) — must be a no-op stub.
    procedure CallAlterKeyOnPK(clustered: Boolean)
    var
        RecRef: RecordRef;
        KRef: KeyRef;
    begin
        RecRef.Open(Database::"DAK Item");
        KRef := RecRef.KeyIndex(1);
        Database.AlterKey(KRef, clustered);
        RecRef.Close();
    end;

    /// Proving helper — returns a+b+1 so tests can verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
