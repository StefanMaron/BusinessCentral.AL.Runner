table 65922 "XPD Blob Holder"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { DataClassification = CustomerContent; }
        field(2; "Blob Data"; Blob) { DataClassification = CustomerContent; }
    }

    keys { key(PK; "Entry No.") { Clustered = true; } }
}
