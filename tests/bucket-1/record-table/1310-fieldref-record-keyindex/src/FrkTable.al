/// Table used by FieldRef.Record().KeyIndex tests.
/// Two-field PK (Id, Code) so KeyRef.FieldCount = 2.
table 1310001 "FRK Test Entry"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Code; Code[20]) { }
        field(3; Description; Text[100]) { }
    }
    keys
    {
        key(PK; Id, Code) { Clustered = true; }
    }
}
