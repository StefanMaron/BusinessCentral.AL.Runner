table 55200 "Record Get Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Code; Code[20]) { }
        field(2; Description; Text[50]) { }
        field(3; Quantity; Integer) { }
    }

    keys
    {
        key(PK; Code) { Clustered = true; }
    }
}

table 55210 "Record Get Composite"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; CompanyNo; Code[10]) { }
        field(2; EntryNo; Integer) { }
        field(3; Amount; Decimal) { }
    }

    keys
    {
        key(PK; CompanyNo, EntryNo) { Clustered = true; }
    }
}
