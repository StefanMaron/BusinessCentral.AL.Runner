table 63000 "TFV Item"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No.";   Code[20])    { }
        field(2; "Name";  Text[100])   { }
        field(3; "Qty";   Integer)     { }
        field(4; "Price"; Decimal)     { }
        field(5; "Active"; Boolean)    { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
