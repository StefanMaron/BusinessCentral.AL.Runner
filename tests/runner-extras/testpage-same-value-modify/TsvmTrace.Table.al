/// Out-of-row tally. Has no OnModify of its own, so Bump's own Modify cannot recurse.
table 65782 "Tsvm Trace"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Modify Count"; Integer) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }

    procedure Reset()
    var
        Trace: Record "Tsvm Trace";
    begin
        Trace.DeleteAll();
        Trace.Init();
        Trace."Entry No." := 1;
        Trace."Modify Count" := 0;
        Trace.Insert();
    end;

    procedure Bump()
    var
        Trace: Record "Tsvm Trace";
    begin
        if not Trace.Get(1) then begin
            Trace.Init();
            Trace."Entry No." := 1;
            Trace."Modify Count" := 0;
            Trace.Insert();
        end;
        Trace."Modify Count" += 1;
        Trace.Modify();
    end;

    procedure Count(): Integer
    var
        Trace: Record "Tsvm Trace";
    begin
        if not Trace.Get(1) then
            exit(0);
        exit(Trace."Modify Count");
    end;
}
