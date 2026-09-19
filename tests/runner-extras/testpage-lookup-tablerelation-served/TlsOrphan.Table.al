// A related table that declares NO LookupPageId. A "Tls Host" field pointing here has a
// TableRelation the runner can resolve and a page it cannot find, which is a different refusal
// from having no relation at all -- and the two must not share a message, because a message
// saying "the lookup comes from a TableRelation" is wrong for a field that has none.
table 65792 "Tls Orphan"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
