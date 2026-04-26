table 311001 "RecRef WritePerm Table"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Description"; Text[100]) { }
    }

    keys
    {
        key(PK; "Entry No.") { }
    }
}

codeunit 311002 "RecRef WritePerm Helper"
{
    procedure TestWritePermission(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RecRef WritePerm Table");
        // WritePermission returns true in standalone mode (no permission enforcement)
        exit(RecRef.WritePermission);
    end;

    procedure TestWritePermissionOnClosedRef(): Boolean
    var
        RecRef: RecordRef;
    begin
        // WritePermission on an unopen RecordRef; always true in standalone
        exit(RecRef.WritePermission);
    end;

    procedure TestWritePermissionAfterClose(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RecRef WritePerm Table");
        RecRef.Close();
        // WritePermission after close; always true in standalone
        exit(RecRef.WritePermission);
    end;
}
