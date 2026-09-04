/// <summary>The source rows the header's FlowFields aggregate.</summary>
table 60841 "FFR Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Doc No."; Code[20]) { }
        field(3; Amount; Decimal) { }
        field(4; "Ref Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
