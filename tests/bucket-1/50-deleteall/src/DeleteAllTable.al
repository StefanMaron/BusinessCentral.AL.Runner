table 55600 "Delete All Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Status; Integer) { }
        field(3; Name; Text[50]) { }
    }
    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
