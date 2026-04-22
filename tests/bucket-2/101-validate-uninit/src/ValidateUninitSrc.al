/// Table with a global var section and an OnValidate trigger on a field.
/// The OnValidate trigger reads a global Record variable. When
/// TryFireOnValidateInType calls GetUninitializedObject, the global
/// var's backing field is null unless InitializeUninitializedObject is called.
table 100003 "Validate Uninit Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; PK; Code[20]) { }
        field(2; "Rate Code"; Code[10])
        {
            trigger OnValidate()
            var
                Position: Integer;
            begin
                // Access a global Record variable — this forces the runtime
                // to resolve the backing field created by the var section.
                // Without proper initialization after GetUninitializedObject,
                // accessing Helper.PK would throw NullReferenceException.
                Position := Helper.PK;
                if "Rate Code" = 'SPECIAL' then
                    Position := 99;
                Rec."Mapped Position" := Position;
            end;
        }
        field(3; "Mapped Position"; Integer) { }
    }

    keys
    {
        key(PK; PK) { Clustered = true; }
    }

    var
        Helper: Record "Validate Helper Table";
}

table 100004 "Validate Helper Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; PK; Integer) { }
        field(2; "Helper Value"; Text[50]) { }
    }

    keys
    {
        key(PK; PK) { Clustered = true; }
    }
}
