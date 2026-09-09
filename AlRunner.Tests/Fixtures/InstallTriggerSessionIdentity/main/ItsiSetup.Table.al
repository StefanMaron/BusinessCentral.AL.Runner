// The setup row the install trigger keys on the session identity — the shape AlRunner#3268's
// reproducer describes (Setup."Owner Security ID" := UserSecurityId()).
table 70780 "ITSI Setup"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[10]) { DataClassification = CustomerContent; }
        field(2; "Owner Security ID"; Guid) { DataClassification = CustomerContent; }
        field(3; "Owner User Name"; Text[50]) { DataClassification = CustomerContent; }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
