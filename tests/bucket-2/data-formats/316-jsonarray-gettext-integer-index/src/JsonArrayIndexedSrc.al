/// Source helpers for JsonArray integer-index GetText/GetInteger/GetDecimal/GetBoolean/GetArray
/// overloads. Reproduces issue #1426: CS1503 int→string when passing an Integer index
/// to JsonArray.GetText() / GetInteger() / GetDecimal() / GetBoolean() / GetArray().
codeunit 316100 "Json Array Indexed Src"
{
    /// JsonArray.GetText(Integer index) — reproduces CS1503 from issue #1426.
    procedure GetTextAtIndex(Cols: JsonArray; Index: Integer): Text
    begin
        exit(Cols.GetText(Index));
    end;

    /// JsonArray.GetInteger(Integer index)
    procedure GetIntegerAtIndex(Arr: JsonArray; Index: Integer): Integer
    begin
        exit(Arr.GetInteger(Index));
    end;

    /// JsonArray.GetDecimal(Integer index)
    procedure GetDecimalAtIndex(Arr: JsonArray; Index: Integer): Decimal
    begin
        exit(Arr.GetDecimal(Index));
    end;

    /// JsonArray.GetBoolean(Integer index)
    procedure GetBooleanAtIndex(Arr: JsonArray; Index: Integer): Boolean
    begin
        exit(Arr.GetBoolean(Index));
    end;

    /// JsonArray.GetArray(Integer index) — nested array
    procedure GetArrayAtIndex(Arr: JsonArray; Index: Integer): JsonArray
    begin
        exit(Arr.GetArray(Index));
    end;
}
