// Target table for install-trigger seeding. The Subtype=Install codeunit
// (60711) inserts rows here from its lifecycle triggers; the tests assert the
// exact rows exist BEFORE any test code ran.
table 60710 "Install Seed"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Value"; Integer) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
