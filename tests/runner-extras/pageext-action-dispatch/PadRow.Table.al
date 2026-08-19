/// Backing table for the pageextension-action-dispatch suite. Log() rows are the observable
/// proof an OnAction trigger actually ran (and which one) — the whole point per #1923: a
/// silent no-op passes "no exception thrown" but writes nothing here.
table 64520 "Pad Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Descr; Text[100]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }

    procedure Log(Tag: Code[20])
    var
        Row: Record "Pad Row";
    begin
        if not Row.Get(Tag) then begin
            Row.Init();
            Row."No." := Tag;
            Row.Insert();
        end;
    end;

    procedure HasMessage(Tag: Code[20]): Boolean
    var
        Row: Record "Pad Row";
    begin
        exit(Row.Get(Tag));
    end;
}
