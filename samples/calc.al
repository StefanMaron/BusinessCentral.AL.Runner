codeunit 50101 "Calculator"
{
    trigger OnRun()
    var
        x: Decimal;
        y: Decimal;
        result: Decimal;
    begin
        x := 42;
        y := 8;
        result := x + y;
        Message('The answer is %1', result);
    end;
}
