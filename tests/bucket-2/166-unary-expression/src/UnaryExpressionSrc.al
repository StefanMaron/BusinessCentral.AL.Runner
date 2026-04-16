/// Helper codeunit exercising AL unary operators: `-x`, `+x`, `not b`.
codeunit 59860 "UNE Src"
{
    procedure Negate(n: Integer): Integer
    begin
        exit(-n);
    end;

    procedure NegateDecimal(d: Decimal): Decimal
    begin
        exit(-d);
    end;

    procedure Identity(n: Integer): Integer
    begin
        exit(+n);
    end;

    procedure LogicalNot(b: Boolean): Boolean
    begin
        exit(not b);
    end;

    procedure NotInBranch(b: Boolean): Text
    begin
        if not b then
            exit('was-false')
        else
            exit('was-true');
    end;

    procedure NegateInExpression(a: Integer; b: Integer): Integer
    begin
        exit(a + -b);
    end;

    procedure NegateNegative(n: Integer): Integer
    begin
        exit(-(-n));
    end;
}
