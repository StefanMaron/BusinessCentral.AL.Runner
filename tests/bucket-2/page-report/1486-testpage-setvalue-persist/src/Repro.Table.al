table 500000 "Repro SetValue Tab"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Integer) { }
        field(2; Description; Text[50]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
