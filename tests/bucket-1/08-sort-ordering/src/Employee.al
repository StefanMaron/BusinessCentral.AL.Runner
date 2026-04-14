table 50108 "Test Employee"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "Name"; Text[100])
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Salary"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(4; "Department"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
        key(BySalary; "Salary")
        {
        }
        key(ByName; "Name")
        {
        }
    }
}
