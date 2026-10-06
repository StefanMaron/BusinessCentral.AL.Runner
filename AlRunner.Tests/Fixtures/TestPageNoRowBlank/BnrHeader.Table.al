// Fixture for codeunit 73400 "BNR Blank Part Tests": the host row a part is linked to.
table 73400 "BNR Header"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
