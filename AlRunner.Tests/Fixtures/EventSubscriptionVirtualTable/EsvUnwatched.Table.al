// The negative arm's publisher: a table with NO subscriber anywhere in this fixture.
// Deliberately subscriber-free — adding an [EventSubscriber] for it silently inverts
// EventSubscription_UnsubscribedTable_HasNoRows rather than failing it.
table 70762 "ESV Unwatched"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
    }

    keys { key(PK; "Entry No.") { Clustered = true; } }
}
