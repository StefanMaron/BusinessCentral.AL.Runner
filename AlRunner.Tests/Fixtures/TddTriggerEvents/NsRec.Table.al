namespace TrgNs;

/// <summary>#5286: a table in a namespace, extended by an extension that names it with the namespace.</summary>
table 72309 "Trg Ns Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }
}
