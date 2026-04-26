/// Table used by the TestField NavValue-subtype overload test suite.
/// Exercises Record.TestField(FieldNo, Value) where Value is a NavValue subtype
/// (e.g. NavInteger) — the pattern that triggered CS0121 ambiguity.
table 161000 "TFNav Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Qty; Integer) { }
        field(3; Name; Text[100]) { }
        field(4; Amt; Decimal) { }
        field(5; Flag; Boolean) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
