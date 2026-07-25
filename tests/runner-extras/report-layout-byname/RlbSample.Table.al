// Fixture table for the layout-by-name suite. The report iterates it; the Blob
// field is the SaveAs-to-OutStream target.
table 61871 "RLB Sample"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Description"; Text[100]) { }
        field(10; "Blob Data"; Blob) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
