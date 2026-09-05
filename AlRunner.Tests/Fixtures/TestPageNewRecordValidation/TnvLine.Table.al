// Line table for the New()-stamping suite. The field set is chosen so each of the four claims
// has its own witness, and none of them can be satisfied by accident:
//
//   "No."      PK, stamped by a field(...) link  -> its OnValidate MUST run
//   Kind       PK, stamped by a const(...) link  -> its OnValidate MUST run (the arm nothing
//                                                   else pins: the corpus test uses field(...))
//   "Line No." PK, NOT named by any link         -> its OnValidate MUST NOT run
//   Descr      not PK, NOT named by any link     -> its OnValidate MUST NOT run
//
// The last two are what turn "New() validates" into "New() validates the STAMPED SET", which is
// the runner-side claim: BC hands ValidateFieldsAsync exactly fieldsInitializedFromFilters, so
// validating more than that would be just as wrong as validating nothing.
//
// "No. CurrFieldNo" records CurrFieldNo as seen INSIDE "No."'s own OnValidate. See
// TestPageNewRecordValidationTests for why the expected value is 0 and why that is a choice
// worth pinning rather than leaving implicit.
table 70401 "TNV Line"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = SystemMetadata;
            trigger OnValidate()
            begin
                "No. Validated" := true;
                "No. CurrFieldNo" := CurrFieldNo;
            end;
        }
        field(2; Kind; Code[10])
        {
            DataClassification = SystemMetadata;
            trigger OnValidate()
            begin
                "Kind Validated" := true;
            end;
        }
        field(3; "Line No."; Integer)
        {
            DataClassification = SystemMetadata;
            trigger OnValidate()
            begin
                "Line No. Validated" := true;
            end;
        }
        field(4; Descr; Text[50])
        {
            DataClassification = SystemMetadata;
            trigger OnValidate()
            begin
                "Descr Validated" := true;
            end;
        }
        field(5; "No. Validated"; Boolean) { DataClassification = SystemMetadata; }
        field(6; "Kind Validated"; Boolean) { DataClassification = SystemMetadata; }
        field(7; "Line No. Validated"; Boolean) { DataClassification = SystemMetadata; }
        field(8; "Descr Validated"; Boolean) { DataClassification = SystemMetadata; }
        field(9; "No. CurrFieldNo"; Integer) { DataClassification = SystemMetadata; }
    }

    keys { key(PK; "No.", Kind, "Line No.") { Clustered = true; } }
}
