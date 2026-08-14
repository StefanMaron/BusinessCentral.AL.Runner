/// <summary>
/// A plain data table defined in the DEPENDENCY app, with no tableextension
/// declared anywhere in this bundle. See tests/runner-extras/server-reload-main
/// for the tableextension a LATER --server request adds to this same table
/// (issue #1860).
/// </summary>
table 64400 "SRW Item"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; Log; Text[100]) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
