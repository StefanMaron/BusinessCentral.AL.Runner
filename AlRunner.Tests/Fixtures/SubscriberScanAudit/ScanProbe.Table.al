/// <summary>
/// Trivial table so this fixture has something to compile and run against, beyond the
/// app.json floor that is the actual point of this fixture. See app.json's description.
/// </summary>
table 61200 "SSA Scan Probe"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; "Value"; Integer) { }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}
