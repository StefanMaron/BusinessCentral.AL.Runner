table 50119 "Product"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "Code"; Code[20])
        {
            DataClassification = ToBeClassified;
        }
        field(2; "First Name"; Text[50])
        {
            DataClassification = ToBeClassified;
        }
        field(3; "Last Name"; Text[50])
        {
            DataClassification = ToBeClassified;
        }
        field(4; "Quantity"; Integer)
        {
            DataClassification = ToBeClassified;
        }
        field(5; "Limit"; Integer)
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
    }

    procedure GetDisplayName(): Text
    begin
        exit("First Name" + ' ' + "Last Name");
    end;

    procedure IsOverLimit(): Boolean
    begin
        exit("Quantity" > "Limit");
    end;

    procedure RemainingCapacity(): Integer
    begin
        if "Quantity" >= "Limit" then
            exit(0);
        exit("Limit" - "Quantity");
    end;
}
