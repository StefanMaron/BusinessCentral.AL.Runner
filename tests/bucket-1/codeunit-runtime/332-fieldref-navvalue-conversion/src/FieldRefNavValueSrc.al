table 1320424 "FieldRef Conv Table"
{
    fields
    {
        field(1; "No."; Code[20]) { }
    }

    keys
    {
        key(PK; "No.") { }
    }
}

codeunit 1320425 "FieldRef NavValue Src"
{
    procedure StrSubstNo_WithFieldRef(): Text
    var
        Rec: Record "FieldRef Conv Table";
        RecRef: RecordRef;
        FRef: FieldRef;
    begin
        Rec."No." := 'FR1';
        RecRef.GetTable(Rec);
        FRef := RecRef.Field(1);
        exit(StrSubstNo('%1', FRef));
    end;
}
