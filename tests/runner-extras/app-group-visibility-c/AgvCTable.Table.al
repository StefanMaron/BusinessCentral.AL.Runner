table 62620 "AGV C Table"
{
    Caption = 'AGV C Table Caption';
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
