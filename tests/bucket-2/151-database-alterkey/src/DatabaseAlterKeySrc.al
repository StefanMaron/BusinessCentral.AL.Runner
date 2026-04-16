/// Helper codeunit that wraps Database.AlterKey so the test can exercise it.
codeunit 61000 "DAK Helper"
{
    /// Call Database.AlterKey as a no-op stub.
    /// Parameters mirror the real AL signature: Clustered, KeyName, TableName.
    procedure CallAlterKey(clustered: Boolean; keyName: Text; tableName: Text)
    begin
        Database.AlterKey(clustered, keyName, tableName);
    end;

    /// Proving helper — returns a+b+1 so tests can verify the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
