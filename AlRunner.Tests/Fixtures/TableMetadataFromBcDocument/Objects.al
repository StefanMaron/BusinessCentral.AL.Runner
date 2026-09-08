// Every declaration here is a non-default value that BC's emitted metadata document carries
// and the runner's AL-source derivation did not reproduce, so each is a distinguishable
// assertion rather than a coincidence with a default:
//
//   field 2  Editable = false          — the derivation left every field editable
//   field 2  EndUserIdentifiableInformation, field 3 SystemMetadata
//                                      — the derivation gave every field the TABLE's value
//   field 3  Enum "TMD Kind"           — the derivation left EnumTypeId 0 / EnumTypeName empty
//
// Field 1 declares none of the three, so it is the control: it must stay editable and take
// the table's CustomerContent.

enum 70680 "TMD Kind"
{
    Extensible = true;

    value(0; Plain) { Caption = 'Plain'; }
    value(5; Fancy) { Caption = 'Fancy'; }
}

table 70680 "TMD Thing"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Description; Text[50])
        {
            DataClassification = EndUserIdentifiableInformation;
            Editable = false;
        }
        field(3; Kind; Enum "TMD Kind")
        {
            DataClassification = SystemMetadata;
        }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
        key(ByDescription; Description) { }
    }
}
