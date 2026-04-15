table 56260 "Metadata Test Item"
{
    Caption = 'Test Item';

    fields
    {
        field(1; "Entry No."; Integer)
        {
            Caption = 'Entry Number';
        }
        field(2; Description; Text[100])
        {
            Caption = 'Item Description';
        }
        field(3; Amount; Decimal)
        {
        }
        field(4; "Item Code"; Code[20])
        {
            Caption = 'Code';
        }
        field(5; Active; Boolean)
        {
        }
        field(6; "Vendor Name"; Text[50])
        {
            Caption = 'Vendor''s Name';
        }
    }
    keys { key(PK; "Entry No.") { Clustered = true; } }
}
