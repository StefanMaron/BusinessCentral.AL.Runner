table 54200 "TF Src"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Name"; Text[50]) { }
        field(3; "Amount"; Decimal) { }
        field(4; "Active"; Boolean) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

table 54201 "TF Tgt"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Name"; Text[50]) { }
        field(3; "Amount"; Decimal) { }
        field(5; "Extra"; Text[50]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
