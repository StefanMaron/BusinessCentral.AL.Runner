/// Helper codeunit that sets a Code[20] field via FieldRef (NavText) and reads it back directly.
/// This triggers the NavText stored in Code field slot → cast fails on direct access.
codeunit 98400 "NTC Helper"
{
    /// Set a Code field via FieldRef (value arrives as NavText) then read it back directly.
    /// This is the exact pattern that causes InvalidCastException:
    ///   FieldRef.Value := textValue  → SetFieldValue(fieldNo, NavType.Text, NavText)
    ///   record."Category"           → (NavCode)GetFieldValueSafe(fieldNo, NavType.Code) → FAIL
    procedure InsertAndReadViaFieldRef(Id: Integer; CategoryValue: Text[100]): Code[20]
    var
        RecRef: RecordRef;
        FldId: FieldRef;
        FldCat: FieldRef;
        Rec: Record "NTC Table";
    begin
        // Set up record via RecordRef/FieldRef — this stores NavText in the Code field slot
        RecRef.Open(Database::"NTC Table");
        RecRef.Init();
        FldId := RecRef.Field(1);
        FldId.Value := Id;
        FldCat := RecRef.Field(2);
        FldCat.Value := CategoryValue;  // NavText stored in Code[20] slot
        RecRef.Insert();
        RecRef.Close();

        // Now read the Code field directly — BC codegen emits (NavCode)GetFieldValueSafe(2, NavType.Code)
        // If the stored value is NavText, this cast fails with InvalidCastException.
        Rec.Get(Id);
        exit(Rec."Category");
    end;

    /// Insert using a Code literal (baseline — should always work).
    procedure InsertWithCodeCategory(Id: Integer; Category: Code[20])
    var
        Rec: Record "NTC Table";
    begin
        Rec.Init();
        Rec."Id" := Id;
        Rec."Category" := Category;
        Rec.Insert();
    end;

    /// Read the Category field back as Code.
    procedure GetCategory(Id: Integer): Code[20]
    var
        Rec: Record "NTC Table";
    begin
        Rec.Get(Id);
        exit(Rec."Category");
    end;
}
