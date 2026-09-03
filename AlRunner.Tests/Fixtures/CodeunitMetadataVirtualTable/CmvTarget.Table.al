// The table "CMV Bound" points at through its TableNo property, so the CodeUnit Metadata
// TableNo column has a real object id to resolve to rather than 0.
table 60761 "CMV Target"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
