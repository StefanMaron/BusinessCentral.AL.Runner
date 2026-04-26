codeunit 50020 "Loop Helper"
{
    procedure SumRange(FromVal: Integer; ToVal: Integer): Integer
    var
        i: Integer;
        Total: Integer;
    begin
        Total := 0;
        for i := FromVal to ToVal do
            Total += i;
        exit(Total);
    end;

    procedure CollectEvenOdd(Count: Integer)
    var
        i: Integer;
    begin
        for i := 1 to Count do
            if i mod 2 = 0 then
                Message('even: %1', i)
            else
                Message('odd: %1', i);
    end;
}
