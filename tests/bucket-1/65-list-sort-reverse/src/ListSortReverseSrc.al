codeunit 58000 "LSR List Helper"
{
    procedure ReverseIntegerList(var Items: List of [Integer])
    begin
        Items.Reverse();
    end;

    procedure ReverseTextList(var Items: List of [Text])
    begin
        Items.Reverse();
    end;
}
