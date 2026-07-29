// Mirrors the shape a real asset registry uses: a row is identified by
// (Scope, SourceAppId, Name, StyleVariant), and resolution ranges Scope/Name/StyleVariant
// while EXCLUDING rows whose owning-app GUID was never set.

enum 62180 "GFC Scope"
{
    Extensible = false;

    value(0; Tenant) { Caption = 'Tenant'; }
    value(1; Extension) { Caption = 'Extension'; }
}

enum 62181 "GFC Style Variant"
{
    Extensible = false;

    value(0; Regular) { Caption = 'Regular'; }
    value(1; Bold) { Caption = 'Bold'; }
}

table 62180 "GFC Asset"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; Scope; Enum "GFC Scope") { }
        field(2; SourceAppId; Guid) { }
        field(3; Name; Code[50]) { }
        field(4; StyleVariant; Enum "GFC Style Variant") { }
        field(5; Payload; Text[30]) { }
    }

    keys
    {
        key(PK; Scope, SourceAppId, Name, StyleVariant) { Clustered = true; }
    }
}
