table 60460 "RFG Row"
{
    fields
    {
        field(1; "Id"; Integer) { }
    }
    keys { key(PK; "Id") { Clustered = true; } }
}

/// Exercises RecordRef.FilterGroup.
codeunit 60460 "RFG Src"
{
    procedure FilterGroup_SetThenGet(group: Integer): Integer
    var
        rr: RecordRef;
    begin
        rr.Open(60460);
        rr.FilterGroup(group);
        exit(rr.FilterGroup());
    end;

    procedure FilterGroup_DefaultIsZero(): Integer
    var
        rr: RecordRef;
    begin
        rr.Open(60460);
        exit(rr.FilterGroup());
    end;
}
