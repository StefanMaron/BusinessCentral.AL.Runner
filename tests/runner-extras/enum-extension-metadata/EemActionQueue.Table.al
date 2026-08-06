table 62303 "EEM Action Queue"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Action Type"; Enum "EEM Action Type") { }
        // ^ Creates the circular reference: Enum -> Interface -> Record -> same Enum.
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}
