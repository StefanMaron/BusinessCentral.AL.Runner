codeunit 56270 "API Probe"
{
    /// <summary>
    /// Helper codeunit to exercise RecordRef/FieldRef/KeyRef API methods.
    /// Each procedure isolates a specific API call for testing.
    /// </summary>

    procedure InsertEntry(EntryNo: Integer; Cat: Code[20]; Desc: Text[100]; Amt: Decimal; IsActive: Boolean)
    var
        R: Record "API Test Entry";
    begin
        R."Entry No." := EntryNo;
        R.Category := Cat;
        R.Description := Desc;
        R.Amount := Amt;
        R.Active := IsActive;
        R.Insert();
    end;

    procedure GetAscendingDefault(): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        exit(RecRef.Ascending);
    end;

    procedure IterateFieldValuesDescending(FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Result: Text;
    begin
        RecRef.Open(Database::"API Test Entry");
        RecRef.Ascending(false);
        if RecRef.FindSet() then
            repeat
                FldRef := RecRef.Field(FieldNo);
                if Result <> '' then
                    Result += ',';
                Result += Format(FldRef.Value);
            until RecRef.Next() = 0;
        RecRef.Close();
        exit(Result);
    end;

    procedure MarkRecordByEntryNo(EntryNo: Integer)
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(EntryNo);
        if RecRef.FindFirst() then
            RecRef.Mark(true);
        RecRef.Close();
    end;

    procedure IsRecordMarked(EntryNo: Integer): Boolean
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(EntryNo);
        if RecRef.FindFirst() then
            exit(RecRef.Mark());
        exit(false);
    end;

    procedure IterateMarkedOnly(): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Result: Text;
    begin
        RecRef.Open(Database::"API Test Entry");
        // Mark entries 1 and 3
        FldRef := RecRef.Field(1);
        FldRef.SetRange(1);
        if RecRef.FindFirst() then
            RecRef.Mark(true);
        FldRef.SetRange(3);
        if RecRef.FindFirst() then
            RecRef.Mark(true);
        // Reset filters and iterate marked only
        RecRef.Reset();
        RecRef.MarkedOnly(true);
        if RecRef.FindSet() then
            repeat
                FldRef := RecRef.Field(1);
                if Result <> '' then
                    Result += ',';
                Result += Format(FldRef.Value);
            until RecRef.Next() = 0;
        RecRef.Close();
        exit(Result);
    end;

    procedure MarkAndClearMarks(): Boolean
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(1);
        if RecRef.FindFirst() then
            RecRef.Mark(true);
        RecRef.ClearMarks();
        // After ClearMarks, Mark() should return false
        FldRef.SetRange(1);
        if RecRef.FindFirst() then
            exit(RecRef.Mark());
        exit(false);
    end;

    procedure GetFilterAfterSetRange(): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(10, 50);
        exit(FldRef.GetFilter());
    end;

    procedure GetFilterAfterSetRangeSingle(): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(42);
        exit(FldRef.GetFilter());
    end;

    procedure GetFilterAfterSetFilter(): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(3);
        FldRef.SetFilter('*test*');
        exit(FldRef.GetFilter());
    end;

    procedure GetRangeMinAfterSetRange(): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Val: Integer;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(10, 50);
        Val := FldRef.GetRangeMin();
        exit(Val);
    end;

    procedure GetRangeMaxAfterSetRange(): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        Val: Integer;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(10, 50);
        Val := FldRef.GetRangeMax();
        exit(Val);
    end;

    procedure GetRangeMinMaxSingle(): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        MinVal: Integer;
        MaxVal: Integer;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(10);
        MinVal := FldRef.GetRangeMin();
        MaxVal := FldRef.GetRangeMax();
        exit(Format(MinVal) + ',' + Format(MaxVal));
    end;

    procedure FieldRefRecordOwner(): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
        OwnerRef: RecordRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(1);
        OwnerRef := FldRef.Record();
        exit(OwnerRef.Number);
    end;

    procedure GetKeyCount(): Integer
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        exit(RecRef.KeyCount);
    end;

    procedure GetKeyFieldCount(): Integer
    var
        RecRef: RecordRef;
        KRef: KeyRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        KRef := RecRef.KeyIndex(1);
        exit(KRef.FieldCount);
    end;

    procedure GetKeyFieldNo(FieldIdx: Integer): Integer
    var
        RecRef: RecordRef;
        KRef: KeyRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        KRef := RecRef.KeyIndex(1);
        FldRef := KRef.FieldIndex(FieldIdx);
        exit(FldRef.Number());
    end;

    procedure GetCurrentKeyIndex(): Integer
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        exit(RecRef.CurrentKeyIndex);
    end;

    procedure GetRecRefGetFilter(FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(FieldNo);
        FldRef.SetRange(5, 15);
        // Use FieldRef.GetFilter to verify the filter expression (exercises MockRecordRef.GetFieldFilter delegation)
        exit(FldRef.GetFilter());
    end;

    procedure GetRecRefGetFilterNoFilter(FieldNo: Integer): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        // No filter set — verify delegation returns empty string
        RecRef.Open(Database::"API Test Entry");
        FldRef := RecRef.Field(FieldNo);
        exit(FldRef.GetFilter());
    end;

    procedure FindLastEntryNoWithMarkedOnly(): Text
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"API Test Entry");
        // Mark only entry 2 (the middle one)
        FldRef := RecRef.Field(1);
        FldRef.SetRange(2);
        if RecRef.FindFirst() then
            RecRef.Mark(true);
        RecRef.Reset();
        RecRef.MarkedOnly(true);
        if RecRef.FindLast() then begin
            FldRef := RecRef.Field(1);
            exit(Format(FldRef.Value));
        end;
        exit('');
    end;
}
