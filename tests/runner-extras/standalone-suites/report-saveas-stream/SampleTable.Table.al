// Fixture table for the SaveAs dataset spine. The report iterates this table;
// the test inserts a row with a known marker value and asserts the dataset XML
// carries it. The Blob field is the stream target for SaveAs-to-OutStream.
table 60701 "RSS Sample"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Description"; Text[100]) { }
        field(3; "Amount"; Decimal) { }
        field(10; "Blob Data"; Blob) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
