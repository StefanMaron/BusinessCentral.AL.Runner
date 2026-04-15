table 54900 "Init Value Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Id; Integer) { }
        field(2; Status; Integer) { InitValue = 1; }
        field(3; Name; Text[50]) { InitValue = 'Default'; }
        field(4; Active; Boolean) { InitValue = true; }
        field(5; Amount; Decimal) { InitValue = 9.99; }
        field(6; NoInit; Integer) { }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}
