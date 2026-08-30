/// Backing table for the promoted-actionref suite. A Log() row is the observable proof that a
/// specific OnAction trigger actually ran: "Invoke() did not throw" is worth nothing here,
/// because the pre-#2113 failure mode on one arm was a silent no-op rather than a throw.
table 64540 "Par Row"
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
        Row: Record "Par Row";
    begin
        if not Row.Get(Tag) then begin
            Row.Init();
            Row."No." := Tag;
            Row.Insert();
        end;
    end;
}
