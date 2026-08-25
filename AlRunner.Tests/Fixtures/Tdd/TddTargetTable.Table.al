/// <summary>
/// The "implementing app" table half of the fixture. Deliberately does NOT declare a
/// "Loyalty Points" field yet — TddBrokenFieldTests.Codeunit.al references it as if it
/// already existed, the shape a test-first developer writes before the implementation
/// catches up.
/// </summary>
table 65001 "Tdd Target Table"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
