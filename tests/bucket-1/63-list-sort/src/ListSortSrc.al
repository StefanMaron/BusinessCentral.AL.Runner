/// Helper that wraps List.Sort() calls so they can be tested via asserterror
/// when Sort() is not available, or via direct assertion when it is.
codeunit 81100 "List Sort Helper"
{
    procedure SortIntegerList(var Items: List of [Integer])
    begin
        Items.Sort();
    end;

    procedure SortTextList(var Items: List of [Text])
    begin
        Items.Sort();
    end;
}
