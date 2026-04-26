/// Table with a Code[20] field used for NavText→NavCode cast testing.
table 98400 "NTC Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Id"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Category"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Id")
        {
            Clustered = true;
        }
    }
}
