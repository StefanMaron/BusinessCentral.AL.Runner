table 57800 "FA Test"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Name"; Text[100]) { }
        field(3; "Amount"; Decimal) { }
        field(4; "Qty"; Integer) { }
        field(5; "Active"; Boolean) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}

codeunit 57801 "FA Helper"
{
    procedure IsFieldActiveByNo(RecRef: RecordRef; FieldNo: Integer): Boolean
    var
        FRef: FieldRef;
    begin
        FRef := RecRef.Field(FieldNo);
        exit(FRef.Active());
    end;

    procedure CountActiveFields(RecRef: RecordRef): Integer
    var
        FRef: FieldRef;
        I: Integer;
        Cnt: Integer;
    begin
        for I := 1 to RecRef.FieldCount() do begin
            FRef := RecRef.FieldIndex(I);
            if FRef.Active() then
                Cnt += 1;
        end;
        exit(Cnt);
    end;
}
