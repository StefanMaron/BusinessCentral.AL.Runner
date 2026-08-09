/// <summary>
/// The source table the FlowFields in "CFS Header" aggregate. The seeded rows differ in
/// Amount, Open and Status so that each where-condition shape selects a DIFFERENT subset.
/// </summary>
table 64260 "CFS Line"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Doc No."; Code[20]) { }
        field(3; Amount; Decimal) { }
        field(4; Open; Boolean) { }
        field(5; Status; Option)
        {
            OptionMembers = Open,Released,Closed;
        }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
