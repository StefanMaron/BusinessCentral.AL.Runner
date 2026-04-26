table 56870 "SR Test Item"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Code; Code[20]) { }
        field(4; Status; Option) { OptionMembers = Open,Closed,Pending; }
        field(5; Amount; Decimal) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
