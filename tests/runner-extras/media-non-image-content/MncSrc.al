table 62040 "MNC Asset"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Content; Media) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
