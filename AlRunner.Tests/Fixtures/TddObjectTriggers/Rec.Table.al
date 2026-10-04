table 73002 "OT Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer) { trigger OnValidate() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRecValidate(1); end; }
    }
    keys { key(PK; PK) { Clustered = true; } }
    trigger OnInsert() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRecInsert(1); end;
    trigger OnModify() var T: Codeunit "OT Target"; R: Integer; begin R := T.MRecModify(1); end;
}

table 73011 "OT Store"
{
    fields { field(1; PK; Code[20]) { } field(2; Data; Blob) { } }
    keys { key(PK; PK) { Clustered = true; } }
    trigger OnInsert() var T: Codeunit "OT Target"; R: Integer; begin R := T.MStoreInsert(1); end;
}
