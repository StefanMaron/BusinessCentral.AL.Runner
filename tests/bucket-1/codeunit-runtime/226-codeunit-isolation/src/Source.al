/// Tables for codeunit-isolation tests.
/// BC's default TestIsolation is Codeunit: table state is shared across all
/// test methods in the same codeunit, and reset between codeunits.
table 226001 "CI Shared Table"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; Id; Integer) { }
        field(2; Value; Integer) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
