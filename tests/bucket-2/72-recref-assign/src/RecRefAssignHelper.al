table 56710 "RecRef Assign Data"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Description"; Text[100]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

codeunit 56710 "RecRef Assign Helper"
{
    /// <summary>
    /// Opens a RecordRef, inserts a record, assigns it to another RecordRef,
    /// and returns the Description field from the assigned copy.
    /// </summary>
    procedure InsertAndAssign(EntryNo: Integer; Desc: Text[100]): Text[100]
    var
        RecRef1: RecordRef;
        RecRef2: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef1.Open(Database::"RecRef Assign Data");
        FldRef := RecRef1.Field(1);
        FldRef.Value := EntryNo;
        FldRef := RecRef1.Field(2);
        FldRef.Value := Desc;
        RecRef1.Insert();

        // Assign RecRef1 to RecRef2 — this is the `:=` operator which
        // the BC compiler lowers to ALAssign.
        RecRef2 := RecRef1;

        // Now read the description from RecRef2
        FldRef := RecRef2.Field(2);
        exit(FldRef.Value);
    end;

    /// <summary>
    /// Verifies that after assignment, the two RecordRefs share the same table number.
    /// </summary>
    procedure AssignedTableNo(): Integer
    var
        RecRef1: RecordRef;
        RecRef2: RecordRef;
    begin
        RecRef1.Open(Database::"RecRef Assign Data");
        RecRef2 := RecRef1;
        exit(RecRef2.Number);
    end;
}
