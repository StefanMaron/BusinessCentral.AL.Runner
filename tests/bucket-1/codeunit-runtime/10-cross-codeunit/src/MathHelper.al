codeunit 50110 "Math Helper"
{
    procedure Square(X: Integer): Integer
    begin
        exit(X * X);
    end;

    procedure Factorial(N: Integer): Integer
    var
        Result: Integer;
        I: Integer;
    begin
        if N <= 1 then
            exit(1);
        Result := 1;
        for I := 2 to N do
            Result := Result * I;
        exit(Result);
    end;

    procedure Max(A: Integer; B: Integer): Integer
    begin
        if A > B then
            exit(A);
        exit(B);
    end;
}
