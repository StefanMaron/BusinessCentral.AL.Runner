/// <summary>
/// The source table for "WPMR List" — see that page's OnOpenPage trigger for what
/// this fixture actually proves.
/// </summary>
table 70020 "WPMR Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; Touched; Boolean) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
