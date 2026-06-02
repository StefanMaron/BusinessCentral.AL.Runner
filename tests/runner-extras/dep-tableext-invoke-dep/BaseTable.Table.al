/// <summary>
/// Base table defined in the dependency app.
/// The consuming app extends this table via a tableextension in the dep app,
/// then calls extension code from the main app to prove InvokeAsync(extId) fires.
/// </summary>
table 60700 "DEX Base Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Name"; Text[100]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
