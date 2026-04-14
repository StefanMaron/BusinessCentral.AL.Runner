/// <summary>
/// Probe codeunit for test 87 — FieldRef.SetRange with various argument types.
/// Each FilteredCount variant exercises a different argument type to ensure
/// the ALSetRange overload resolution does not produce CS0121.
/// </summary>
codeunit 56870 "SR Probe"
{
    /// <summary>Count rows where field 1 (Integer) matches intValue.</summary>
    procedure FilteredCountByInt(IntValue: Integer): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"SR Test Item");
        FldRef := RecRef.Field(1);
        FldRef.SetRange(IntValue);
        exit(RecRef.Count());
    end;

    /// <summary>Count rows where field 3 (Code) matches codeValue.</summary>
    procedure FilteredCountByCode(CodeValue: Code[20]): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"SR Test Item");
        FldRef := RecRef.Field(3);
        FldRef.SetRange(CodeValue);
        exit(RecRef.Count());
    end;

    /// <summary>Count rows where field 4 (Option) matches optionValue.</summary>
    procedure FilteredCountByOption(OptionValue: Option): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"SR Test Item");
        FldRef := RecRef.Field(4);
        FldRef.SetRange(OptionValue);
        exit(RecRef.Count());
    end;

    /// <summary>Count rows where field 5 (Decimal) is in [fromVal..toVal].</summary>
    procedure FilteredCountByDecimalRange(FromVal: Decimal; ToVal: Decimal): Integer
    var
        RecRef: RecordRef;
        FldRef: FieldRef;
    begin
        RecRef.Open(Database::"SR Test Item");
        FldRef := RecRef.Field(5);
        FldRef.SetRange(FromVal, ToVal);
        exit(RecRef.Count());
    end;

    /// <summary>Insert a row via typed record.</summary>
    procedure InsertRow(Id: Integer; Name: Text[100]; Code: Code[20]; Status: Option; Amount: Decimal)
    var
        R: Record "SR Test Item";
    begin
        R.Id := Id;
        R.Name := Name;
        R.Code := Code;
        R.Status := Status;
        R.Amount := Amount;
        R.Insert();
    end;
}
