/// Helper codeunit exercising the global AL math functions Abs, Power, Round.
codeunit 60030 "MATH Src"
{
    procedure AbsDecimal(v: Decimal): Decimal
    begin
        exit(Abs(v));
    end;

    procedure AbsInteger(v: Integer): Integer
    begin
        exit(Abs(v));
    end;

    procedure PowerIt(base: Decimal; exponent: Decimal): Decimal
    begin
        exit(Power(base, exponent));
    end;

    procedure RoundTwoArg(v: Decimal; precision: Decimal): Decimal
    begin
        exit(Round(v, precision));
    end;

    procedure RoundThreeArg(v: Decimal; precision: Decimal; direction: Text[1]): Decimal
    begin
        exit(Round(v, precision, direction));
    end;

    procedure RoundOneArg(v: Decimal): Decimal
    begin
        // Default precision is 1 (rounds to nearest integer as Decimal).
        exit(Round(v));
    end;
}
