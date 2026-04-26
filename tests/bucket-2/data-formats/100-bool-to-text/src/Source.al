table 170001 "BTT Bool Table"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Flag; Boolean) { }
        field(3; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

codeunit 170002 "BTT Bool Helper"
{
    /// Copy a Boolean FieldRef.Value into a Text FieldRef.Value, then read the text field.
    /// The BC transpiler stores the Boolean field via MockRecordRef with NavType.Text, and
    /// later reads the Text field with (NavText)GetFieldValueSafe(3, NavType.Text).
    /// If CoerceToExpectedType doesn't convert NavBoolean→NavText the cast throws:
    ///   "Unable to cast NavBoolean to NavText"
    procedure CopyBoolFieldRefToTextField(var Rec: Record "BTT Bool Table"): Text[100]
    var
        RecRef: RecordRef;
        FldRefBool: FieldRef;
        FldRefText: FieldRef;
    begin
        RecRef.GetTable(Rec);
        FldRefBool := RecRef.Field(2);     // Boolean field
        FldRefText := RecRef.Field(3);     // Text[100] field
        FldRefText.Value := FldRefBool.Value;   // store bool value in text slot
        RecRef.SetTable(Rec);
        exit(Rec.Name);                    // reads text field back as (NavText)GetFieldValueSafe
    end;

    procedure GetFlagAsText(var Rec: Record "BTT Bool Table"): Text
    begin
        exit(Format(Rec.Flag));
    end;
}
