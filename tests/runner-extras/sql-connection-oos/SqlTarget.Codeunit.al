// The statement under test, wrapped so the tests can reach it bare and through a TryFunction.
codeunit 65682 "Sql Target"
{
    // Database.AlterKey(KeyRef, Boolean) enables or disables a secondary key in SQL Server. BC's
    // body reaches NavSqlConnectionScope.Create through NavSqlBatchedCommand; no runner patch
    // stands in front of it.
    procedure AlterSecondaryKey()
    var
        Rec: RecordRef;
        KeyRef: KeyRef;
    begin
        Rec.Open(Database::"Sql Probe Tbl");
        KeyRef := Rec.KeyIndex(2);
        Database.AlterKey(KeyRef, false);
    end;

    [TryFunction]
    procedure TryAlterSecondaryKey()
    begin
        AlterSecondaryKey();
    end;
}
