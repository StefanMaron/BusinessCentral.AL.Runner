table 59400 "Fixed Section Test Table"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }

    fieldgroups
    {
        fieldgroup(DropDown; Id, Name) { }
        fieldgroup(Fixed; Id, Name, Amount) { }
    }
}
