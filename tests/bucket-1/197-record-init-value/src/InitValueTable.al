/// Table with several field types and InitValue declarations for Init() tests.
table 100400 "IV Test Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Priority; Integer)
        {
            InitValue = 5;
        }
        field(3; IsActive; Boolean)
        {
            InitValue = true;
        }
        field(4; Score; Decimal)
        {
            InitValue = 9.99;
        }
        field(5; Description; Text[100]) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
