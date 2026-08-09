/// Base table behind the page and the pageextension in this bundle.
table 64220 "EOM Item"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Description"; Text[100]) { }
        field(3; "Note"; Text[100]) { }
    }

    keys { key(PK; "Code") { Clustered = true; } }
}
