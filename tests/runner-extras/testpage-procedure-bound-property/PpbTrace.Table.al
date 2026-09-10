/// Which OnAction triggers ran. A tag row is inserted by the trigger, so "the trigger did not
/// run" is the absence of that row -- a concrete, checkable value rather than a page global the
/// test could not read after Close.
table 65912 "Ppb Trace"
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
        Trace: Record "Ppb Trace";
    begin
        if Trace.Get(NewTag) then
            exit;
        Trace.Init();
        Trace.Tag := NewTag;
        Trace.Insert();
    end;

    procedure Ran(WantedTag: Code[20]): Boolean
    var
        Trace: Record "Ppb Trace";
    begin
        exit(Trace.Get(WantedTag));
    end;
}
