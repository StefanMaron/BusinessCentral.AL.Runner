table 70000 "Probe Doc"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Status; Enum "Doc Status") { } // declared by no app: --tdd generates it
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
