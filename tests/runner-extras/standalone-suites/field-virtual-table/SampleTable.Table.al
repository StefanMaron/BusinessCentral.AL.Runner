// A small table with known, concrete field metadata. The virtual Field table
// (2000000041) must enumerate exactly these fields. A BLOB field and the
// primary-key field 1 are present specifically so the EnableWorkflow filter set
// (No.<>1, Type<>BLOB) can be proven to behave as BC does.
table 60601 "VFT Sample"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }      // primary key — EnableWorkflow filters No.<>1
        field(2; "Description"; Text[100]) { }   // a normal field that MUST be enumerated
        field(3; "Amount"; Decimal) { }          // a second normal field
        field(10; "Blob Data"; Blob) { }         // BLOB — EnableWorkflow filters Type<>BLOB
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
