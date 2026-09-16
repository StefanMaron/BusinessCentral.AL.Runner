// The publisher table. Its OnAfterInsertEvent has a subscriber in "ESV Subscriber", so the
// Event Subscription table must hold a row naming this table as publisher.
table 70761 "ESV Watched"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
    }

    keys { key(PK; "Entry No.") { Clustered = true; } }
}
