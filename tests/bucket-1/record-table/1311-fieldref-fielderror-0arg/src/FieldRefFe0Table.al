/// Table for testing FieldRef.FieldError() with 0 args (issue #1428).
table 1311001 "FieldRef FE0 Table"
{
    DataClassification = SystemMetadata;

    fields
    {
        field(1; "Code"; Code[10])
        {
            Caption = 'Code';
        }
        field(2; "Description"; Text[100])
        {
            Caption = 'Description';
        }
    }

    keys
    {
        key(PK; "Code")
        {
            Clustered = true;
        }
    }
}

codeunit 1311002 "FieldRef FE0 Helper"
{
    /// <summary>
    /// Calls FieldRef.FieldError() with NO arguments via RecordRef/FieldRef
    /// on field 2 (Description) of the given record's key value.
    /// </summary>
    procedure CallFieldErrorNoArgs(KeyCode: Code[10])
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"FieldRef FE0 Table");
        FldRef := RecRef.Field(2);
        FldRef.FieldError();
    end;

    /// <summary>
    /// Calls FieldRef.FieldError(Text) with ONE argument for comparison.
    /// </summary>
    procedure CallFieldErrorOneArg(KeyCode: Code[10]; Msg: Text)
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"FieldRef FE0 Table");
        FldRef := RecRef.Field(2);
        FldRef.FieldError(Msg);
    end;
}
