// Codeunit used by emit-resilience tests (issue #1554).
// Exercises try-function error propagation — the pattern the emit-resilience
// fix ensures can be compiled even when unrelated AL in the same compilation
// batch triggers BC emit exceptions.
codeunit 50085 "Emit Resilience Helper"
{
    procedure DivideInts(Numerator: Integer; Denominator: Integer): Integer
    begin
        if Denominator = 0 then
            Error('Division by zero');
        exit(Numerator div Denominator);
    end;

    [TryFunction]
    procedure TryDivide(Numerator: Integer; Denominator: Integer; var Result: Integer)
    begin
        Result := DivideInts(Numerator, Denominator);
    end;

    procedure SafeDivide(Numerator: Integer; Denominator: Integer; var Result: Integer): Boolean
    begin
        exit(TryDivide(Numerator, Denominator, Result));
    end;

    procedure ConcatWithSeparator(A: Text; Separator: Text; B: Text): Text
    begin
        if A = '' then
            exit(B);
        if B = '' then
            exit(A);
        exit(A + Separator + B);
    end;
}
