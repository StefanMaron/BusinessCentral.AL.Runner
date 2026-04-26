table 56242 "FF Order Header"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(10; "Line Count"; Integer)
        {
            CalcFormula = count("FF Order Line" where("Order No." = field("No.")));
            FieldClass = FlowField;
        }
        field(11; "Total Amount"; Decimal)
        {
            CalcFormula = sum("FF Order Line"."Amount" where("Order No." = field("No.")));
            FieldClass = FlowField;
        }
        field(12; "First Item"; Code[20])
        {
            CalcFormula = lookup("FF Order Line"."Item No." where("Order No." = field("No.")));
            FieldClass = FlowField;
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }
}
