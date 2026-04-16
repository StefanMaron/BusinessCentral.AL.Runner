/// Helper codeunit that exercises List.RemoveRange(StartIndex, Count).
/// RemoveRange removes Count elements starting at StartIndex (1-based).
codeunit 61100 "LRR Helper"
{
    /// Build [1,2,3,4,5], remove 3 elements from index 2, return remaining list.
    procedure BuildAndRemoveMiddle(var Result: List of [Integer])
    var
        Items: List of [Integer];
    begin
        Items.Add(1);
        Items.Add(2);
        Items.Add(3);
        Items.Add(4);
        Items.Add(5);
        Items.RemoveRange(2, 3);
        Result := Items;
    end;

    /// Build [10,20,30], remove 1 element from index 1, return remaining list.
    procedure BuildAndRemoveFirst(var Result: List of [Integer])
    var
        Items: List of [Integer];
    begin
        Items.Add(10);
        Items.Add(20);
        Items.Add(30);
        Items.RemoveRange(1, 1);
        Result := Items;
    end;

    /// Build [1,2,3], remove all via RemoveRange, return Count.
    procedure RemoveAllReturnCount(): Integer
    var
        Items: List of [Integer];
    begin
        Items.Add(1);
        Items.Add(2);
        Items.Add(3);
        Items.RemoveRange(1, 3);
        exit(Items.Count());
    end;

    /// Attempt RemoveRange with out-of-bounds index — should raise an error.
    procedure RemoveOutOfRange()
    var
        Items: List of [Integer];
    begin
        Items.Add(1);
        Items.RemoveRange(10, 1);
    end;
}
