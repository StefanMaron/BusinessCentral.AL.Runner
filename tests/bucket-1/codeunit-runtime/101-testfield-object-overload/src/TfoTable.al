/// Table with an Integer field exercising TestField inside a table procedure.
/// Reproduces CS1503 'object' → 'NavValue' when the BC transpiler emits
/// ALTestFieldNavValueSafe(fieldNo, NavType, intObjectValue) for Rec.TestField("Table No.").
table 307100 "TFO Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "No."; Code[20])
        {
            DataClassification = CustomerContent;
        }
        field(2; "Table No."; Integer)
        {
            DataClassification = CustomerContent;
        }
        field(3; "Name"; Text[50])
        {
            DataClassification = CustomerContent;
        }
    }

    keys
    {
        key(PK; "No.")
        {
            Clustered = true;
        }
    }

    /// Called from within the table — exercises ALTestFieldNavValueSafe on the Record class
    /// with an Integer field. The BC transpiler may emit object-typed values here (CS1503).
    procedure ValidateTableNo()
    begin
        Rec.TestField("Table No.");
    end;

    /// TestField with explicit Integer value — exercises ALTestFieldNavValueSafe(object) overload.
    procedure ValidateTableNoEquals(ExpectedNo: Integer)
    begin
        Rec.TestField("Table No.", ExpectedNo);
    end;

    /// TestField on Name field — exercises ALTestFieldNavValueSafe for Text field.
    procedure ValidateName()
    begin
        Rec.TestField("Name");
    end;

    /// TestField on Name with value — exercises ALTestFieldNavValueSafe(object) for Text.
    procedure ValidateNameEquals(ExpectedName: Text[50])
    begin
        Rec.TestField("Name", ExpectedName);
    end;
}
