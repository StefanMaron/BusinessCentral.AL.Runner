/// <summary>
/// "Self Ref Amount" is a FlowField whose where-condition names ITSELF, so calculating
/// it could only be done by calculating it. BC refuses such a formula outright with its
/// recursion error rather than recursing until the stack is exhausted, and the runner
/// reproduces that refusal in FlowFieldPatches' recursion guards. That refusal is the
/// EXPECTED result here — the fixture's test traps it with asserterror.
/// </summary>
table 60842 "FFR Header"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }

        /// A plain, terminating parent-link FlowField — the control that proves a refused
        /// self-referencing formula does not disturb the rest of the table.
        field(10; "Total Amount"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("FFR Line".Amount where("Doc No." = field("No.")));
        }

        /// The self-referencing formula.
        field(11; "Self Ref Amount"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("FFR Line".Amount where("Doc No." = field("No."),
                                                     "Ref Amount" = field("Self Ref Amount")));
        }

        /// A pair of FlowFields whose where-conditions reference EACH OTHER. Neither names
        /// itself, so the cycle is only caught by the depth bound — which makes the runner's
        /// diagnostic trace hundreds of frames long instead of two.
        field(12; "Cycle A"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("FFR Line".Amount where("Doc No." = field("No."),
                                                     "Ref Amount" = field("Cycle B")));
        }

        field(13; "Cycle B"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("FFR Line".Amount where("Doc No." = field("No."),
                                                     "Ref Amount" = field("Cycle A")));
        }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
