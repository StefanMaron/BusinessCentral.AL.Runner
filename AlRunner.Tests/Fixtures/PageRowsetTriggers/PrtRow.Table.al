table 70640 "PRT Row"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { DataClassification = CustomerContent; }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}
