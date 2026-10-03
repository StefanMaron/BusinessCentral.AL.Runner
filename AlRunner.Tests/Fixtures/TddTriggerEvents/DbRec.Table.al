/// <summary>
/// #5286: no triggers and no event publishers of its own; the subscribers of its database events
/// are in "Trg Db Subscriber".
/// </summary>
table 72303 "Trg Db Rec"
{
    fields
    {
        field(1; PK; Code[20]) { }
        field(2; Qty; Integer) { }
    }
    keys { key(PK; PK) { Clustered = true; } }
}
