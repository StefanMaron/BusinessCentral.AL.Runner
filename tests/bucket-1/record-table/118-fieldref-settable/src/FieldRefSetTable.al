// Renumbered from 50120 to avoid collision in new bucket layout (#1385).
table 1050120 "FieldRef Test Table"
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

// FieldRef.ALSetTable is emitted by the BC compiler for page API
// extension code — it is NOT callable from AL directly. This test
// exercises RecRef/FieldRef interaction to prove the types compile
// and the data round-trips correctly via SetTable.
// Renumbered from 50120 to avoid collision in new bucket layout (#1385).
codeunit 1050120 "FieldRef SetTable Helper"
{
    procedure SetTableCopiesData(var EntryNo: Integer; var Desc: Text[100])
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Rec: Record "FieldRef Test Table";
    begin
        Rec."Entry No." := 42;
        Rec.Description := 'SetTableTest';
        Rec.Insert();

        RecRef.Open(Database::"FieldRef Test Table");
        RecRef.FindFirst();
        // Access a FieldRef before SetTable to prove both coexist
        FldRef := RecRef.Field(2);
        RecRef.SetTable(Rec);
        EntryNo := Rec."Entry No.";
        Desc := Rec.Description;
    end;
}
