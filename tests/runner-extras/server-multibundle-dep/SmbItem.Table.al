/// <summary>
/// A plain data table defined in the DEPENDENCY app. The sibling main app's
/// tests CRUD records of this table, so its NCLMetaTable must be built from
/// this bundle's parsed AL source and must survive until the sibling bundle
/// actually runs (server mode used to reset it away between bundles).
///
/// The "Description" field is the schema-change probe for the server-mode
/// integration script (scripts/tests/server-mode-test.sh): the script deletes
/// it between two warm requests and expects the next compile to MISS the
/// AL-output cache and fail loudly.
/// </summary>
table 64300 "SMB Item"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; Description; Text[100]) { }
        field(3; Quantity; Integer) { }
    }

    keys
    {
        key(PK; "Code") { Clustered = true; }
    }
}
