table 62170 "VFC Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        // A deliberately two-word caption: the AL that consumes this builds an identifier from
        // it, so the exact caption text is what matters, not merely that a row is found.
        field(20; NewColumnName; Text[30]) { Caption = 'New Column Name'; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
