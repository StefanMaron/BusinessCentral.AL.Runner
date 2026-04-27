table 1313100 "Repro FRValVar Tab"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Integer) { }
        field(2; Amount; Decimal)
        {
            trigger OnValidate()
            begin
                if Amount < 0 then
                    Error('Amount must be non-negative');
            end;
        }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
