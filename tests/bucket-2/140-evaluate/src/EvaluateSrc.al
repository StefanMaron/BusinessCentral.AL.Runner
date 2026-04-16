/// Helper codeunit that exercises the AL built-in Evaluate() function.
/// Evaluate(var Variable; Text) parses a text value into a typed variable.
/// Returns true on success, false on failure.
codeunit 60200 "EVL Helper"
{
    /// Parse text into Integer. Returns parsed value; errors on invalid input.
    procedure ParseInteger(txt: Text): Integer
    var
        result: Integer;
    begin
        if not Evaluate(result, txt) then
            Error('Evaluate failed for integer: %1', txt);
        exit(result);
    end;

    /// Returns true if the text parses as a valid Integer.
    procedure TryParseInteger(txt: Text): Boolean
    var
        result: Integer;
    begin
        exit(Evaluate(result, txt));
    end;

    /// Parse text into Boolean.
    procedure ParseBoolean(txt: Text): Boolean
    var
        result: Boolean;
    begin
        if not Evaluate(result, txt) then
            Error('Evaluate failed for boolean: %1', txt);
        exit(result);
    end;

    /// Parse text into Decimal.
    procedure ParseDecimal(txt: Text): Decimal
    var
        result: Decimal;
    begin
        if not Evaluate(result, txt) then
            Error('Evaluate failed for decimal: %1', txt);
        exit(result);
    end;

    /// Parse text into Date.
    procedure ParseDate(txt: Text): Date
    var
        result: Date;
    begin
        if not Evaluate(result, txt) then
            Error('Evaluate failed for date: %1', txt);
        exit(result);
    end;
}
