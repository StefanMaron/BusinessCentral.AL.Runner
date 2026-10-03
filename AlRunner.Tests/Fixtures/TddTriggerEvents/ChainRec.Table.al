/// <summary>
/// #5286: an insert starts the first of two subscribers; the stub is called by the second, which a procedure
/// of the first starts. A search that stops after one round finds the second only.
/// </summary>
table 72305 "Trg Chain Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}

table 72306 "Trg Chain Rec 2"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}
