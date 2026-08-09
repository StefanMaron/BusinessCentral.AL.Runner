/// <summary>
/// One FlowField per CalcFormula shape under test. With the seeded rows (see "CFS Tests")
/// every one of them lands on a different value, so an implementation that ignored the
/// sign, ignored the where-conditions, or returned the type default cannot satisfy them.
/// </summary>
table 64261 "CFS Header"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }

        /// The baseline: no condition beyond the parent link. 100 + 20 + 3 = 123.
        field(10; "Total Amount"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("CFS Line".Amount where("Doc No." = field("No.")));
        }

        /// #1708 — the same aggregate with AL's leading sign. Must be -123, not 123 and
        /// not 0 (0 is what the refused-formula behaviour produced).
        field(11; "Negated Total"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = -sum("CFS Line".Amount where("Doc No." = field("No.")));
        }

        /// #1709 — const() on a Boolean. Only the two Open rows: 100 + 3 = 103.
        field(12; "Open Total"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("CFS Line".Amount where("Doc No." = field("No."),
                                                     Open = const(true)));
        }

        /// #1709 — const() driving a count. Exactly one row is not Open.
        field(13; "Closed Count"; Integer)
        {
            FieldClass = FlowField;
            CalcFormula = count("CFS Line" where("Doc No." = field("No."),
                                                 Open = const(false)));
        }

        /// #1709 — filter() alternation on an Option field: Open|Released = 100 + 20 = 120.
        field(14; "Active Total"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("CFS Line".Amount where("Doc No." = field("No."),
                                                     Status = filter(Open | Released)));
        }

        /// #1709 — filter() range on an Integer field: entries 2..3 = 20 + 3 = 23.
        field(15; "Range Total"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("CFS Line".Amount where("Doc No." = field("No."),
                                                     "Entry No." = filter(2 .. 3)));
        }

        /// #1709, the exclusion direction — two const conditions that no row satisfies
        /// together (the only Closed row is Open). Must be 0, which only holds if BOTH
        /// conditions are applied and ANDed.
        field(16; "Unmatched Total"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = sum("CFS Line".Amount where("Doc No." = field("No."),
                                                     Status = const(Closed),
                                                     Open = const(false)));
        }

        /// #1708 + #1709 together: the negated sum of the Released rows only = -20.
        field(17; "Negated Released Total"; Decimal)
        {
            FieldClass = FlowField;
            CalcFormula = -sum("CFS Line".Amount where("Doc No." = field("No."),
                                                      Status = const(Released)));
        }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
