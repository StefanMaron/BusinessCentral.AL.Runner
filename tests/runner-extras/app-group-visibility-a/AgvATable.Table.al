table 62600 "AGV A Table"
{
    Caption = 'AGV A Table Caption';
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
