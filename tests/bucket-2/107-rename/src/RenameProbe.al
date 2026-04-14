table 95100 "Rename Probe"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Description; Text[50]) { }
        field(3; Amount; Decimal) { }
    }
    keys { key(PK; "Entry No.") { Clustered = true; } }
}

table 95101 "Rename Composite"
{
    fields
    {
        field(1; "Doc Type"; Integer) { }
        field(2; "Doc No."; Code[20]) { }
        field(3; "Line No."; Integer) { }
        field(4; Description; Text[50]) { }
    }
    keys { key(PK; "Doc Type", "Doc No.", "Line No.") { Clustered = true; } }
}
