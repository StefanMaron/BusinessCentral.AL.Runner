table 50120 "FieldRef Test Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Description; Text[100]) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

codeunit 50120 "FieldRef SetTable Helper"
{
    procedure TestFieldRefSetTable(): Boolean
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Rec: Record "FieldRef Test Table";
    begin
        RecRef.Open(Database::"FieldRef Test Table");
        FldRef := RecRef.Field(2);
        // SetTable should compile and not error
        RecRef.SetTable(Rec);
        exit(true);
    end;
}
