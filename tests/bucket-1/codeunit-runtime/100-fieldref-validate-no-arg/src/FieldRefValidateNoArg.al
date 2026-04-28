table 1320513 "FR Validate NoArg"
{
    fields
    {
        field(1; "No."; Integer) { }
        field(2; Amount; Decimal)
        {
            trigger OnValidate()
            begin
                if Amount < 0 then
                    Error('Amount must be non-negative');
                Validated := true;
            end;
        }
        field(3; Validated; Boolean) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
