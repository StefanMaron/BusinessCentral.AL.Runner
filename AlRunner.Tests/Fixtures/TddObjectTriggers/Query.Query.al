query 73004 "OT Query"
{
    elements { dataitem(Row; "OT Rec") { column(PK; PK) { } } }
    trigger OnBeforeOpen() var T: Codeunit "OT Target"; R: Integer; begin R := T.MQryBefore(1); end;
}

query 73007 "OT Other Query"
{
    elements { dataitem(Row; "OT Rec") { column(PK; PK) { } } }
    trigger OnBeforeOpen() var T: Codeunit "OT Target"; R: Integer; begin R := T.MOtherQryBefore(1); end;
}
