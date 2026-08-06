// Mirror of the issue #1674 repro table: enum-typed field with a quoted blank
// InitValue. Before the fix, ANY Init/Insert of this table threw
// NavNCLInvalidOptionStringException from NCLMetaField.get_InitValue.
table 64202 "BEI Setup"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Primary Key"; Code[10])
        {
            DataClassification = CustomerContent;
        }
        field(2; "Logging Type"; Enum "BEI Blank Logging")
        {
            DataClassification = CustomerContent;
            InitValue = " ";
        }
    }

    keys
    {
        key(PK; "Primary Key")
        {
            Clustered = true;
        }
    }
}
