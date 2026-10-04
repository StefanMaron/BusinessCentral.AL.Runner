table 72801 "PT Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer) { }
        field(3; Note; Text[30]) { }
        // A table lookup a page control without a lookup of its own runs.
        field(4; Ref; Integer)
        {
            trigger OnLookup()
            var T: Codeunit "PT Target"; R: Integer;
            begin
                R := T.MTableLookup(1);
            end;
        }
        field(5; Extra; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

table 72802 "PT Quiet"
{
    fields { field(1; K; Code[20]) { } }
    keys { key(K; K) { Clustered = true; } }
}
