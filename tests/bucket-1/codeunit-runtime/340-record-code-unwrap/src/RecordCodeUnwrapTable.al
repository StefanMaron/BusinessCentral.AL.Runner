table 1320610 "Record Code Unwrap Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Description; Text[50]) { }
    }

    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}
