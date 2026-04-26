table 306000 "RecRef Perm Table"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Amount"; Decimal) { }
        field(3; "Description"; Text[100]) { }
    }

    keys
    {
        key(PK; "Entry No.") { }
    }
}

codeunit 306001 "RecRef Perm Helper"
{
    procedure TestReadPermission(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RecRef Perm Table");
        // ReadPermission returns true in standalone mode (no permission enforcement)
        exit(RecRef.ReadPermission);
    end;

    procedure TestSetAutoCalcFieldsNoThrow(): Boolean
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"RecRef Perm Table");
        FldRef := RecRef.Field(2);
        // SetAutoCalcFields should not throw
        RecRef.SetAutoCalcFields(FldRef.Number);
        exit(true);
    end;

    procedure TestSetAutoCalcFieldsMultiple(): Boolean
    var
        RecRef: RecordRef;
        FldRef2: FieldRef;
        FldRef3: FieldRef;
    begin
        RecRef.Open(Database::"RecRef Perm Table");
        FldRef2 := RecRef.Field(2);
        FldRef3 := RecRef.Field(3);
        // Passing multiple field numbers should not throw
        RecRef.SetAutoCalcFields(FldRef2.Number, FldRef3.Number);
        exit(true);
    end;

    procedure TestReadPermissionOnClosedRef(): Boolean
    var
        RecRef: RecordRef;
    begin
        // ReadPermission on an unopen RecordRef; always true in standalone
        exit(RecRef.ReadPermission);
    end;
}
