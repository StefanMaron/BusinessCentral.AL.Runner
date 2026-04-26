codeunit 1275001 "RecRef Implicit Helper"
{
    procedure GetFieldCountFromRef(RecRef: RecordRef): Integer
    begin
        exit(RecRef.FieldCount);
    end;

    procedure GetTableNoFromRef(RecRef: RecordRef): Integer
    begin
        exit(RecRef.Number);
    end;
}
