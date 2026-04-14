codeunit 50111 "Stats Calculator"
{
    var
        MathHelper: Codeunit "Math Helper";

    procedure SumOfSquares(A: Integer; B: Integer): Integer
    begin
        exit(MathHelper.Square(A) + MathHelper.Square(B));
    end;

    procedure Permutations(N: Integer; R: Integer): Integer
    begin
        if R > N then
            exit(0);
        exit(MathHelper.Factorial(N) div MathHelper.Factorial(N - R));
    end;

    procedure MaxOfThree(A: Integer; B: Integer; C: Integer): Integer
    begin
        exit(MathHelper.Max(MathHelper.Max(A, B), C));
    end;
}
