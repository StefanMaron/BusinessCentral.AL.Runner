codeunit 56680 "RF Probe"
{
    procedure GetFieldValue(TableId: Integer; FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Result: Text;
    begin
        RecRef.Open(TableId);
        RecRef.FindFirst();
        FldRef := RecRef.Field(FieldNo);
        Result := Format(FldRef.Value);
        RecRef.Close();
        exit(Result);
    end;

    procedure SetFieldAndInsert(TableId: Integer; FieldNoId: Integer; IdVal: Integer; FieldNoName: Integer; NameVal: Text)
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNoId);
        FldRef.Value := IdVal;
        FldRef := RecRef.Field(FieldNoName);
        FldRef.Value := NameVal;
        RecRef.Insert();
        RecRef.Close();
    end;

    procedure CountRecords(TableId: Integer): Integer
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableId);
        exit(RecRef.Count());
    end;

    procedure TableNoAfterOpen(TableId: Integer): Integer
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableId);
        exit(RecRef.Number());
    end;

    procedure FieldNumber(TableId: Integer; FieldNo: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        RecRef.FindFirst();
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.Number());
    end;

    procedure IterateNames(TableId: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Result: Text;
    begin
        RecRef.Open(TableId);
        if RecRef.FindSet() then
            repeat
                FldRef := RecRef.Field(2);
                if Result <> '' then
                    Result += ',';
                Result += Format(FldRef.Value);
            until RecRef.Next() = 0;
        RecRef.Close();
        exit(Result);
    end;

    procedure IsTableEmpty(TableId: Integer): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableId);
        exit(RecRef.IsEmpty());
    end;

    procedure DeleteFirstById(TableId: Integer; IdVal: Integer): Boolean
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(1);
        FldRef.SetRange(IdVal);
        if RecRef.FindFirst() then begin
            RecRef.Delete();
            exit(true);
        end;
        exit(false);
    end;

    procedure DeleteAll(TableId: Integer)
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableId);
        RecRef.DeleteAll();
        RecRef.Close();
    end;

    procedure InsertAndModify(TableId: Integer; IdVal: Integer; OrigName: Text; NewName: Text): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        // Insert
        RecRef.Open(TableId);
        FldRef := RecRef.Field(1);
        FldRef.Value := IdVal;
        FldRef := RecRef.Field(2);
        FldRef.Value := OrigName;
        RecRef.Insert();

        // Modify
        FldRef := RecRef.Field(2);
        FldRef.Value := NewName;
        RecRef.Modify();
        RecRef.Close();

        // Read back
        RecRef.Open(TableId);
        RecRef.FindFirst();
        FldRef := RecRef.Field(2);
        exit(Format(FldRef.Value));
    end;

    procedure FilteredCount(TableId: Integer; FieldNo: Integer; FilterValue: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        FldRef.SetRange(FilterValue);
        exit(RecRef.Count());
    end;

    procedure ResetAndCount(TableId: Integer; FieldNo: Integer; FilterValue: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(TableId);
        FldRef := RecRef.Field(FieldNo);
        FldRef.SetRange(FilterValue);
        // Reset should clear the filter
        RecRef.Reset();
        exit(RecRef.Count());
    end;

    procedure CopyRecordToRecRef(var R: Record "RF Test Item"): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.GetTable(R);
        FldRef := RecRef.Field(2);
        exit(Format(FldRef.Value));
    end;

    procedure CopyRecRefToRecord(IdVal: Integer; NameVal: Text; var R: Record "RF Test Item")
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"RF Test Item");
        FldRef := RecRef.Field(1);
        FldRef.Value := IdVal;
        FldRef := RecRef.Field(2);
        FldRef.Value := NameVal;
        RecRef.Insert();
        RecRef.FindFirst();
        RecRef.SetTable(R);
    end;
}
