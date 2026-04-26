table 54300 "FC Three"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Id"; Integer) { }
        field(2; "Name"; Text[50]) { }
        field(3; "Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "Id") { Clustered = true; }
    }
}

table 54301 "FC Five"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Description"; Text[100]) { }
        field(3; "Quantity"; Integer) { }
        field(4; "Active"; Boolean) { }
        field(5; "Notes"; Text[250]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
