table 50100 "Mod All Table"
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
        field(3; "Amount"; Decimal)
        {
        }
        field(4; "Status"; Text[20])
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
