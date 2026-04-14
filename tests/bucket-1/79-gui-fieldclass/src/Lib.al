table 79100 "Test Table 79"
{
    fields
    {
        field(1; "Code"; Code[20]) { }
        field(2; "Name"; Text[100]) { }
        field(3; "Amount"; Decimal) { }
    }
}

codeunit 79100 "Gui FieldClass Lib"
{
    /// Returns true when GUI is available (should be false in standalone runner).
    procedure IsGuiAvailable(): Boolean
    begin
        exit(GuiAllowed);
    end;

    /// Returns true when the field at fieldNo on recRef is FieldClass::Normal.
    procedure IsNormalField(var recRef: RecordRef; fieldNo: Integer): Boolean
    var
        fldRef: FieldRef;
    begin
        fldRef := recRef.Field(fieldNo);
        exit(fldRef.Class = FieldClass::Normal);
    end;

    /// Assigns a RecordRef to a Variant (triggers NavComplexValue context).
    procedure RecRefToVariant(var recRef: RecordRef; var v: Variant)
    begin
        v := recRef;
    end;

    /// Assigns a Variant to a Variant (triggers NavComplexValue context).
    procedure VariantToVariant(var src: Variant; var dest: Variant)
    begin
        dest := src;
    end;
}
