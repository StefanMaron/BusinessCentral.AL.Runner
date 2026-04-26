/// Helper codeunit that exercises List.RemoveRange so tests can call it
/// without managing list state inline.
codeunit 61100 "LRR Helper"
{
    /// Build [1,2,3,4,5] and call RemoveRange(startIndex, count).
    /// Returns the resulting list so the test can assert its contents.
    procedure BuildAndRemoveRange(startIndex: Integer; count: Integer): List of [Integer]
    var
        Items: List of [Integer];
    begin
        Items.Add(1);
        Items.Add(2);
        Items.Add(3);
        Items.Add(4);
        Items.Add(5);
        Items.RemoveRange(startIndex, count);
        exit(Items);
    end;

    /// Return count of elements after a RemoveRange on a single-element list.
    procedure RemoveOnlyElement(): List of [Integer]
    var
        Items: List of [Integer];
    begin
        Items.Add(42);
        Items.RemoveRange(1, 1);
        exit(Items);
    end;

    /// Proving helper — not related to RemoveRange; proves the codeunit is live.
    procedure AddWithBonus(a: Integer; b: Integer): Integer
    begin
        exit(a + b + 1);
    end;
}
