/// <summary>#5286: its database events are subscribed to in the library and in the test bundle, and a table
/// extension of the library adds an OnModify trigger.</summary>
table 72401 "TLib Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}

/// <summary>#5286: the trigger of the app's own table reaches a member generated into the app itself.</summary>
table 72402 "TLib Trig Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }

    trigger OnInsert()
    var
        Target: Codeunit "TLib Target";
        Result: Integer;
    begin
        Result := Target.MissingAppTrigger(1);
    end;
}

/// <summary>#5286, the control: no trigger, nobody subscribes to its events.</summary>
table 72403 "TLib Quiet Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}

/// <summary>#5286: its OnAfterInsertEvent is subscribed to in the test bundle, by a subscriber that
/// reaches the stub only through a second event.</summary>
table 72405 "TLib Chain Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}
