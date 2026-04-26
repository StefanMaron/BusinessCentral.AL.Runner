table 229000 "Query Item"
{
    DataClassification = ToBeClassified;
    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; "Description"; Text[100]) { DataClassification = ToBeClassified; }
        field(3; "Unit Price"; Decimal) { DataClassification = ToBeClassified; }
        field(4; "Quantity"; Integer) { DataClassification = ToBeClassified; }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}
