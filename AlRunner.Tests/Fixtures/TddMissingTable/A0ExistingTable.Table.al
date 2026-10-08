/// <summary>An ordinary table of the app: it takes table id 65320, and a test that uses it runs its real shape.</summary>
table 65320 "Existing Table"
{
    fields
    {
        field(1; "Code"; Integer) { }
        field(2; "Label"; Boolean) { }
    }
    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
