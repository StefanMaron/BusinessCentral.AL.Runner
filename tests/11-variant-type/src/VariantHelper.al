codeunit 50112 "Variant Helper"
{
    procedure FormatAsText(V: Variant): Text
    begin
        exit(Format(V));
    end;

    procedure AddToVariant(BaseValue: Integer; AddValue: Integer; var Result: Variant)
    var
        Sum: Integer;
    begin
        Sum := BaseValue + AddValue;
        Result := Sum;
    end;

    procedure ConcatToVariant(A: Text; B: Text; var Result: Variant)
    var
        Combined: Text;
    begin
        Combined := A + B;
        Result := Combined;
    end;

    procedure DoubleDecimal(Value: Decimal; var Result: Variant)
    var
        Doubled: Decimal;
    begin
        Doubled := Value * 2;
        Result := Doubled;
    end;

    procedure NegateBoolean(Value: Boolean; var Result: Variant)
    var
        Negated: Boolean;
    begin
        Negated := not Value;
        Result := Negated;
    end;
}
