codeunit 50000 Calc
{
    procedure Compute(n: Integer): Integer
    begin
        if n < 0 then
            exit(0);
        exit(n * 2);
    end;
}
