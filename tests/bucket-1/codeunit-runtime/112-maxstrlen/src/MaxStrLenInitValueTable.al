table 296003 "MaxStrLen InitValue Test"
{
    // Table with Text/Code fields that have InitValue declarations.
    // Used to test that MaxStrLen() returns the declared field length
    // even after Init() has populated _fields from the InitValue registry
    // (issue #1506: stored NavText without explicit MaxLength returned Int32.MaxValue).
    fields
    {
        field(1; PK; Integer) { }
        field(2; Msg; Text[100]) { InitValue = 'default'; }
        field(3; ShortCode; Code[10]) { InitValue = 'INIT'; }
    }
    keys
    {
        key(PK; PK) { Clustered = true; }
    }
}
