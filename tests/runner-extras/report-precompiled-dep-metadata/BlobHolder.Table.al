// Scratch blob holder — Report.SaveAs writes into this table's Blob field via
// an OutStream; the test reads it back to inspect the produced dataset XML.
table 61100 "RPD Blob Holder"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Blob Data"; Blob) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
