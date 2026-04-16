/// Helper codeunit exercising JsonArray.Get(Index, var JsonToken).
codeunit 87000 "JAG Src"
{
    /// Get the JsonToken at the given zero-based index.
    procedure GetAt(JA: JsonArray; Index: Integer; var Result: JsonToken): Boolean
    begin
        exit(JA.Get(Index, Result));
    end;

    /// Get the integer value at the given index.
    procedure GetIntAt(JA: JsonArray; Index: Integer): Integer
    var
        JT: JsonToken;
    begin
        JA.Get(Index, JT);
        exit(JT.AsValue().AsInteger());
    end;

    /// Get the text value at the given index.
    procedure GetTextAt(JA: JsonArray; Index: Integer): Text
    var
        JT: JsonToken;
    begin
        JA.Get(Index, JT);
        exit(JT.AsValue().AsText());
    end;

    /// Get the boolean value at the given index.
    procedure GetBoolAt(JA: JsonArray; Index: Integer): Boolean
    var
        JT: JsonToken;
    begin
        JA.Get(Index, JT);
        exit(JT.AsValue().AsBoolean());
    end;

    /// Get the decimal value at the given index.
    procedure GetDecimalAt(JA: JsonArray; Index: Integer): Decimal
    var
        JT: JsonToken;
    begin
        JA.Get(Index, JT);
        exit(JT.AsValue().AsDecimal());
    end;

    /// Build an integer array and return count.
    procedure CountAfterAdd(n: Integer): Integer
    var
        JA: JsonArray;
        i: Integer;
    begin
        for i := 1 to n do
            JA.Add(i);
        exit(JA.Count());
    end;
}
