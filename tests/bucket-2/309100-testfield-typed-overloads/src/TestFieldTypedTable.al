/// Table fixture for the TestField typed/ErrorInfo overload test suite.
/// Covers: Table.TestField(Joker, Decimal), Table.TestField(Joker, Decimal, ErrorInfo),
///         Table.TestField(Joker, DateTime), Table.TestField(Joker, DateTime, ErrorInfo),
///         Table.TestField(Joker, Boolean), Table.TestField(Joker, Boolean, ErrorInfo),
///         Table.TestField(Joker, Integer), Table.TestField(Joker, Integer, ErrorInfo),
///         Table.TestField(Joker, Code),    Table.TestField(Joker, Code, ErrorInfo),
///         Table.TestField(Joker, Text),    Table.TestField(Joker, Text, ErrorInfo),
///         Table.TestField(Joker, ErrorInfo) — issue #1369.
table 309100 "TFTO Item"
{
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[50]) { }
        field(3; Qty; Integer) { }
        field(4; Price; Decimal) { }
        field(5; Active; Boolean) { }
        field(6; "Posted At"; DateTime) { }
    }
    keys { key(PK; "No.") { Clustered = true; } }
}
