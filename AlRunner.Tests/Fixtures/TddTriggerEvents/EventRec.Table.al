/// <summary>
/// #5286: its OnInsert trigger raises an event, and a subscriber of that event calls a missing member
/// (the reviewer's S1 shape: the publisher is a procedure of this app, reached only by Insert(true)).
/// </summary>
table 72302 "Trg Event Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }

    trigger OnInsert()
    begin
        OnTrig();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnTrig()
    begin
    end;
}
