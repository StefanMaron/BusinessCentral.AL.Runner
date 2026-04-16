codeunit 58000 "LSR List Helper"
{
    procedure SortIntegerList(var Items: List of [Integer])
    begin
        Items.Sort();
    end;

    procedure SortTextList(var Items: List of [Text])
    begin
        Items.Sort();
    end;

    procedure ReverseIntegerList(var Items: List of [Integer])
    begin
        Items.Reverse();
    end;

    procedure ReverseTextList(var Items: List of [Text])
    begin
        Items.Reverse();
    end;

    procedure RemoveRangeFromList(var Items: List of [Integer]; Index: Integer; Count: Integer)
    begin
        Items.RemoveRange(Index, Count);
    end;
}
