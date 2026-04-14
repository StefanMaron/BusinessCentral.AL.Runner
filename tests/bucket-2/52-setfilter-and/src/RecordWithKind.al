table 50520 "RWK Row"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Kind; Option)
        {
            OptionMembers = Red,Green,Blue,Yellow;
        }
        field(3; Name; Text[50]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
