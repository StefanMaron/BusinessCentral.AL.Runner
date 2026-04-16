table 84100 "FROM Test Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Status; Option) { OptionMembers = Open,Released,Closed; }
        field(3; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
