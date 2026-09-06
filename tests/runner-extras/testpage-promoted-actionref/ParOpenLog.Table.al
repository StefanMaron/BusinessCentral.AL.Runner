/// Where the RunObject target records its own opening. Separate from "Par Row" on purpose:
/// "Par Row" is the HOST page's source table and the host page is open while the action is
/// invoked, so a write there from the target's OnOpenPage is not a clean observable.
table 64548 "Par Open Log"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Tag; Code[20]) { }
    }

    keys
    {
        key(PK; Tag) { Clustered = true; }
    }

    procedure Log(NewTag: Code[20])
    var
        Entry: Record "Par Open Log";
    begin
        if not Entry.Get(NewTag) then begin
            Entry.Init();
            Entry.Tag := NewTag;
            Entry.Insert();
        end;
    end;
}
