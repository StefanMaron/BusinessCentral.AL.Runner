// Locks the healthy paths that must stay green next to the #1674 fix:
// - blank InitValue on an enum that also has named values,
// - a NAMED InitValue on the same sparse enum (must evaluate to ordinal 5),
// - the classic Option-field blank InitValue (worked before the fix and after).
table 64203 "BEI Mixed"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Primary Key"; Code[10])
        {
            DataClassification = CustomerContent;
        }
        field(2; "Blank Init"; Enum "BEI Mixed Level")
        {
            DataClassification = CustomerContent;
            InitValue = " ";
        }
        field(3; "Named Init"; Enum "BEI Mixed Level")
        {
            DataClassification = CustomerContent;
            InitValue = Verbose;
        }
        field(4; "Option Blank"; Option)
        {
            DataClassification = CustomerContent;
            OptionMembers = " ",Alpha;
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
