table 53300 "Base Item Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
        }
        field(2; "Name"; Text[50])
        {
        }
        field(3; "Category"; Option)
        {
            OptionMembers = "None","Standard","Premium";
        }
        field(4; "Unit Price"; Decimal)
        {
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }
}
