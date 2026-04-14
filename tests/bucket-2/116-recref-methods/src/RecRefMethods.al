table 50116 "RecRef Method Table"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; "Description"; Text[100]) { }
        field(3; "Amount"; Decimal) { }
    }

    keys
    {
        key(PK; "Entry No.") { }
    }
}

codeunit 50117 "RecRef Method Helper"
{
    procedure TestRename(): Boolean
    var
        Rec: Record "RecRef Method Table";
        RecRef: RecordRef;
    begin
        Rec."Entry No." := 1;
        Rec.Description := 'Original';
        Rec.Insert();

        RecRef.Open(Database::"RecRef Method Table");
        RecRef.FindFirst();
        RecRef.Rename(2);
        // After rename PK should be 2
        Rec.Get(2);
        exit(Rec.Description = 'Original');
    end;

    procedure TestGetPosition(): Text
    var
        Rec: Record "RecRef Method Table";
        RecRef: RecordRef;
    begin
        Rec."Entry No." := 1;
        Rec.Description := 'Test';
        Rec.Insert();

        RecRef.Open(Database::"RecRef Method Table");
        RecRef.FindFirst();
        exit(RecRef.GetPosition());
    end;

    procedure TestChangeCompany(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RecRef Method Table");
        // ChangeCompany should not crash (no-op in standalone)
        RecRef.ChangeCompany('CRONUS');
        exit(true);
    end;

    procedure TestMark(): Boolean
    var
        Rec: Record "RecRef Method Table";
        RecRef: RecordRef;
    begin
        Rec."Entry No." := 1;
        Rec.Description := 'MarkTest';
        Rec.Insert();

        RecRef.Open(Database::"RecRef Method Table");
        RecRef.FindFirst();
        // Mark(true) to mark the record
        RecRef.Mark(true);
        // Mark() with no args should return the mark state
        exit(RecRef.Mark());
    end;

    procedure TestAscending(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RecRef Method Table");
        exit(RecRef.Ascending);
    end;

    procedure TestClearMarks()
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RecRef Method Table");
        RecRef.ClearMarks();
    end;

    procedure TestGetFilters(): Text
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"RecRef Method Table");
        exit(RecRef.GetFilters);
    end;

    procedure TestHasFilter(): Boolean
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"RecRef Method Table");
        FldRef := RecRef.Field(2);
        FldRef.SetRange('Test');
        exit(RecRef.HasFilter);
    end;
}

