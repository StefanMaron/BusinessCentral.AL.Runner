table 56630 "OI Probe Row"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Trace; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }

    trigger OnInsert()
    begin
        Rec.Trace := 'touched';
    end;
}

table 56631 "OI Counter"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; Count; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

table 56632 "OI With Side Effect"
{
    fields
    {
        field(1; Id; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }

    trigger OnInsert()
    var
        Counter: Record "OI Counter";
    begin
        if not Counter.Get(1) then begin
            Counter.PK := 1;
            Counter.Count := 1;
            Counter.Insert();
        end else begin
            Counter.Count += 1;
            Counter.Modify();
        end;
    end;
}
