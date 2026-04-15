codeunit 56260 "Metadata Probe"
{
    procedure GetFieldCaption(TableNo: Integer; FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableNo);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.Caption);
    end;

    procedure GetFieldName(TableNo: Integer; FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableNo);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.Name);
    end;

    procedure GetTableName(TableNo: Integer): Text
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableNo);
        exit(RecRef.Name);
    end;

    procedure GetRecordTableCaption(): Text
    var
        Rec: Record "Metadata Test Item";
    begin
        exit(Rec.TableCaption());
    end;

    procedure GetRecordTableName(): Text
    var
        Rec: Record "Metadata Test Item";
    begin
        exit(Rec.TableName());
    end;

    procedure GetFieldType(TableNo: Integer; FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableNo);
        FldRef := RecRef.Field(FieldNo);
        exit(Format(FldRef.Type));
    end;

    procedure GetFieldLength(TableNo: Integer; FieldNo: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableNo);
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.Length);
    end;

    procedure GetRecRefFieldCount(TableNo: Integer): Integer
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableNo);
        exit(RecRef.FieldCount);
    end;
}
