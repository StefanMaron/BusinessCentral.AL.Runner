// A table this suite's own app owns, so PAST Tests can compare the AllObj stamp for an object
// with a KNOWN owner against that owner's Published Application row.
table 65571 "PAST Owned"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = SystemMetadata; }
    }

    keys { key(PK; "Entry No.") { Clustered = true; } }
}
