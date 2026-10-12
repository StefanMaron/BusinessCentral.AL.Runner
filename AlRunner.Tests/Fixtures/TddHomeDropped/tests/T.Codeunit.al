codeunit 65460 "Home Dropped Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure FieldCount_IsTheTablesOwn()
    var
        R: RecordRef;
    begin
        R.Open(65380);
        if R.FieldCount <> 2 then
            Error('FieldCount %1', R.FieldCount);
    end;
}
