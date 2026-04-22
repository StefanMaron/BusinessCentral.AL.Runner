/// Table used by the TestField-with-ErrorInfo test suite.
/// Tests that TestField(Field, Value, ErrorInfo) and TestField(Field, ErrorInfo)
/// compile and behave correctly — resolves CS1501 (4-arg ALTestFieldSafe overload
/// missing) tracked in issues #1083, #1084, #1089.
table 166001 "TFEi Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[50]) { }
        field(3; Flag; Boolean) { }
        field(4; Qty; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
