table 54100 "LF Parent"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Name"; Text[50]) { }
        field(10; "Child Name"; Text[50])
        {
            FieldClass = FlowField;
            CalcFormula = lookup("LF Child".Name where("Parent No." = field("No.")));
        }
        field(11; "Child Amount"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = lookup("LF Child".Amount where("Parent No." = field("No.")));
        }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

table 54101 "LF Child"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Parent No."; Code[20]) { }
        field(3; "Name"; Text[50]) { }
        field(4; "Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
