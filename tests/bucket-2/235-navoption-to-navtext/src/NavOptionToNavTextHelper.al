/// Source for NavOption → NavText runtime conversion tests.
/// Issue #1199: storing an Option/Enum value into a Text field slot via FieldRef
/// assignment caused InvalidCastException:
///   "Object of type NavOption cannot be converted to type NavText"
///
/// The crash happened in two places:
///   1. MockRecordHandle.CoerceToExpectedType — NavOption stored in a Text field slot
///      was not coerced to NavText; later (NavText) cast on GetFieldValueSafe failed.
///   2. MockCodeunitHandle.ConvertArgInternal — NavOption passed as a NavText arg
///      to a reflected method call was not converted, and Convert.ChangeType failed.

enum 235000 "NOT Color"
{
    Extensible = false;

    value(0; " ") { }
    value(1; "Red") { }
    value(2; "Green") { }
    value(3; "Blue") { }
}

table 235000 "NOT Record"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; Id; Integer) { DataClassification = ToBeClassified; }
        field(2; Color; Enum "NOT Color") { DataClassification = ToBeClassified; }
        field(3; ColorText; Text[50]) { DataClassification = ToBeClassified; }
    }

    keys
    {
        key(PK; Id) { Clustered = true; }
    }
}

codeunit 235000 "NOT Helper"
{
    /// <summary>
    /// Copies the Color enum field value to ColorText via FieldRef.
    /// BC emits: colorTextFRef.ALValue = colorFRef.ALValue
    ///   → SetFieldValueSafe(colorTextFieldNo, NavType.Text, navOptionValue)
    /// CoerceToExpectedType must convert NavOption → NavText so that the later
    /// (NavText)GetFieldValueSafe(colorTextFieldNo, NavType.Text) cast succeeds.
    /// </summary>
    procedure CopyEnumToTextViaFieldRef(var Rec: Record "NOT Record")
    var
        RecRef: RecordRef;
        ColorFRef: FieldRef;
        ColorTextFRef: FieldRef;
    begin
        RecRef.GetTable(Rec);
        ColorFRef := RecRef.Field(2);     // Color (Enum "NOT Color")
        ColorTextFRef := RecRef.Field(3); // ColorText (Text[50])
        ColorTextFRef.Value := ColorFRef.Value;
        RecRef.SetTable(Rec);
    end;

    /// <summary>
    /// Returns the ColorText field value after CopyEnumToTextViaFieldRef.
    /// </summary>
    procedure GetColorText(var Rec: Record "NOT Record"): Text
    begin
        exit(Rec.ColorText);
    end;

    /// <summary>
    /// Sets a record with the given Id and Color, copies Color → ColorText
    /// via FieldRef, modifies, then returns ColorText.
    /// Full round-trip exercising NavOption → NavText coercion.
    /// </summary>
    procedure RoundTripEnumToText(Id: Integer; C: Enum "NOT Color"): Text
    var
        Rec: Record "NOT Record";
    begin
        Rec.Init();
        Rec.Id := Id;
        Rec.Color := C;
        Rec.Insert();

        Rec.Get(Id);
        CopyEnumToTextViaFieldRef(Rec);
        exit(Rec.ColorText);
    end;
}
