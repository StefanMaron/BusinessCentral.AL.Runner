table 56640 "RR Mem Row"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 56640 "RR Mem Probe"
{
    procedure HasRows(TableId: Integer): Boolean
    var
        RecRef: RecordRef;
        Result: Boolean;
    begin
        RecRef.Open(TableId);
        Result := not RecRef.IsEmpty();
        RecRef.Close();
        exit(Result);
    end;

    procedure HasRowsInCompany(TableId: Integer; CompanyName: Text): Boolean
    var
        RecRef: RecordRef;
        Result: Boolean;
    begin
        RecRef.Open(TableId, false, CompanyName);
        Result := not RecRef.IsEmpty();
        RecRef.Close();
        exit(Result);
    end;
}
