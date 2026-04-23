table 295001 "Auto Inc Entry"
{
    fields
    {
        field(1; "Entry No."; Integer)
        {
            AutoIncrement = true;
        }
        field(2; Description; Text[100])
        {
        }
        field(3; Amount; Decimal)
        {
        }
    }

    keys
    {
        key(PK; "Entry No.")
        {
            Clustered = true;
        }
    }
}
