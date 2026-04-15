table 55900 "FF Letter"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Code"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Category"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Weight"; Integer)
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "Code")
        {
            Clustered = true;
        }
        key(SK; "Category", "Code")
        {
        }
    }
}
