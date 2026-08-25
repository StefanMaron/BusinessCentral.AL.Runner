/// Minimal fixture table for nav-cancellation-token-1883 (own ID range, see NctTests.Codeunit.al
/// for why this cluster is deleted rather than redirected).
table 60706 "NCT Item"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Value; Integer) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
