table 60300 "RFE Row"
{
    fields
    {
        field(1; "Id"; Integer) { }
        field(2; "Name"; Text[100]) { }
    }
    keys { key(PK; "Id") { Clustered = true; } }
}

/// Exercises RecordRef.FieldExist and RecordRef.RecordLevelLocking.
codeunit 60300 "RFE Src"
{
    procedure FieldExist_ByNo(tableNo: Integer; fieldNo: Integer): Boolean
    var
        rr: RecordRef;
    begin
        rr.Open(tableNo);
        exit(rr.FieldExist(fieldNo));
    end;

    procedure RecordLevelLocking_Get(): Boolean
    var
        rr: RecordRef;
    begin
        rr.Open(60300);
        exit(rr.RecordLevelLocking());
    end;
}
