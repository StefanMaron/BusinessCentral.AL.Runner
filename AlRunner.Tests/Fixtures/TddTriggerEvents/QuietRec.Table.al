/// <summary>
/// #5286, the control: no trigger and nobody subscribes to its database events, so a test that only
/// writes to it reaches no stub.
/// </summary>
table 72304 "Trg Quiet Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}
