/// <summary>Shared source table for both pages in this fixture. See WatchPageMetadataReloadDeleteTests.</summary>
table 70200 "RPR Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
