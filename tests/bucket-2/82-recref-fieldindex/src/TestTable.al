table 50400 "Test Item"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Description"; Text[100]) { }
        field(3; "Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
