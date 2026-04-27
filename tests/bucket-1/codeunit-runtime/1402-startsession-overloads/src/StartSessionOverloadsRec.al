table 1316003 "StartSession Overloads Rec"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; PK; Integer) { }
        field(2; Value; Text[50]) { }
    }

    keys
    {
        key(PK; PK) { Clustered = true; }
    }
}
