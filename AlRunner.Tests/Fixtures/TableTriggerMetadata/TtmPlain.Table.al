// A table declaring NO triggers of its own, so every bit its metadata reports came from
// "TTM Plain Ext" (70742).
table 70740 "TTM Plain"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Payload; Integer) { }
    }

    keys { key(PK; "No.") { Clustered = true; } }
}
