codeunit 58001 "LSR List Sort Reverse Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "LSR List Helper";

    // -----------------------------------------------------------------------
    // List.Sort() — integers
    // -----------------------------------------------------------------------

    [Test]
    procedure Sort_Integer_AscendingOrder()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] An unsorted list of integers
        Items.Add(3);
        Items.Add(1);
        Items.Add(2);

        // [WHEN] Sort() called
        Helper.SortIntegerList(Items);

        // [THEN] Elements are in ascending order
        Assert.AreEqual(1, Items.Get(1), 'First element after Sort must be 1');
        Assert.AreEqual(2, Items.Get(2), 'Second element after Sort must be 2');
        Assert.AreEqual(3, Items.Get(3), 'Third element after Sort must be 3');
    end;

    [Test]
    procedure Sort_Integer_PreservesCount()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list with 4 elements
        Items.Add(10);
        Items.Add(5);
        Items.Add(8);
        Items.Add(1);

        // [WHEN] Sort() called
        Helper.SortIntegerList(Items);

        // [THEN] Count unchanged
        Assert.AreEqual(4, Items.Count(), 'Sort must not change the element count');
    end;

    [Test]
    procedure Sort_Integer_AlreadySorted_NoChange()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] Already sorted list
        Items.Add(1);
        Items.Add(2);
        Items.Add(3);

        // [WHEN] Sort() called
        Helper.SortIntegerList(Items);

        // [THEN] Order unchanged (still sorted)
        Assert.AreEqual(1, Items.Get(1), 'First must still be 1');
        Assert.AreEqual(3, Items.Get(3), 'Last must still be 3');
    end;

    // -----------------------------------------------------------------------
    // List.Sort() — text
    // -----------------------------------------------------------------------

    [Test]
    procedure Sort_Text_AscendingOrder()
    var
        Items: List of [Text];
    begin
        // [GIVEN] Unsorted text list
        Items.Add('Charlie');
        Items.Add('Alpha');
        Items.Add('Beta');

        // [WHEN] Sort() called
        Helper.SortTextList(Items);

        // [THEN] Elements in ascending lexicographic order
        Assert.AreEqual('Alpha', Items.Get(1), 'First text after Sort must be Alpha');
        Assert.AreEqual('Beta', Items.Get(2), 'Second text after Sort must be Beta');
        Assert.AreEqual('Charlie', Items.Get(3), 'Third text after Sort must be Charlie');
    end;

    // -----------------------------------------------------------------------
    // List.Reverse() — integers
    // -----------------------------------------------------------------------

    [Test]
    procedure Reverse_Integer_ReversesOrder()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list [1, 2, 3]
        Items.Add(1);
        Items.Add(2);
        Items.Add(3);

        // [WHEN] Reverse() called
        Helper.ReverseIntegerList(Items);

        // [THEN] Order is reversed: [3, 2, 1]
        Assert.AreEqual(3, Items.Get(1), 'First element after Reverse must be 3');
        Assert.AreEqual(2, Items.Get(2), 'Middle element after Reverse must be 2');
        Assert.AreEqual(1, Items.Get(3), 'Last element after Reverse must be 1');
    end;

    [Test]
    procedure Reverse_Integer_PreservesCount()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list with 5 elements
        Items.Add(10);
        Items.Add(20);
        Items.Add(30);
        Items.Add(40);
        Items.Add(50);

        // [WHEN] Reverse() called
        Helper.ReverseIntegerList(Items);

        // [THEN] Count unchanged
        Assert.AreEqual(5, Items.Count(), 'Reverse must not change the element count');
    end;

    [Test]
    procedure Reverse_ThenReverse_RestoresOriginal()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list [7, 8, 9]
        Items.Add(7);
        Items.Add(8);
        Items.Add(9);

        // [WHEN] Reverse twice
        Helper.ReverseIntegerList(Items);
        Helper.ReverseIntegerList(Items);

        // [THEN] Original order restored
        Assert.AreEqual(7, Items.Get(1), 'Double-reverse: first must be 7');
        Assert.AreEqual(9, Items.Get(3), 'Double-reverse: last must be 9');
    end;

    // -----------------------------------------------------------------------
    // List.Reverse() — text
    // -----------------------------------------------------------------------

    [Test]
    procedure Reverse_Text_ReversesOrder()
    var
        Items: List of [Text];
    begin
        // [GIVEN] A text list ['A', 'B', 'C']
        Items.Add('A');
        Items.Add('B');
        Items.Add('C');

        // [WHEN] Reverse() called
        Helper.ReverseTextList(Items);

        // [THEN] Order reversed: ['C', 'B', 'A']
        Assert.AreEqual('C', Items.Get(1), 'First text after Reverse must be C');
        Assert.AreEqual('A', Items.Get(3), 'Last text after Reverse must be A');
    end;

    // -----------------------------------------------------------------------
    // List.RemoveRange(index, count)
    // -----------------------------------------------------------------------

    [Test]
    procedure RemoveRange_RemovesMiddleElements()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list [10, 20, 30, 40, 50]
        Items.Add(10);
        Items.Add(20);
        Items.Add(30);
        Items.Add(40);
        Items.Add(50);

        // [WHEN] RemoveRange(2, 2) removes elements at index 2 and 3 (20, 30)
        Helper.RemoveRangeFromList(Items, 2, 2);

        // [THEN] Remaining list is [10, 40, 50]
        Assert.AreEqual(3, Items.Count(), 'Count must be 3 after removing 2 elements');
        Assert.AreEqual(10, Items.Get(1), 'First must be 10');
        Assert.AreEqual(40, Items.Get(2), 'Second must be 40');
        Assert.AreEqual(50, Items.Get(3), 'Third must be 50');
    end;

    [Test]
    procedure RemoveRange_RemovesFromStart()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list [1, 2, 3, 4]
        Items.Add(1);
        Items.Add(2);
        Items.Add(3);
        Items.Add(4);

        // [WHEN] RemoveRange(1, 2) removes first two elements
        Helper.RemoveRangeFromList(Items, 1, 2);

        // [THEN] Remaining list is [3, 4]
        Assert.AreEqual(2, Items.Count(), 'Count must be 2 after removing first 2 elements');
        Assert.AreEqual(3, Items.Get(1), 'First must be 3');
        Assert.AreEqual(4, Items.Get(2), 'Second must be 4');
    end;

    [Test]
    procedure RemoveRange_RemovesFromEnd()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list [10, 20, 30]
        Items.Add(10);
        Items.Add(20);
        Items.Add(30);

        // [WHEN] RemoveRange(2, 2) removes last two elements
        Helper.RemoveRangeFromList(Items, 2, 2);

        // [THEN] Remaining list is [10]
        Assert.AreEqual(1, Items.Count(), 'Count must be 1 after removing last 2 elements');
        Assert.AreEqual(10, Items.Get(1), 'Only remaining element must be 10');
    end;

    // -----------------------------------------------------------------------
    // Sort/Reverse interaction
    // -----------------------------------------------------------------------

    [Test]
    procedure SortThenReverse_ProducesDescendingOrder()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] An unsorted list
        Items.Add(3);
        Items.Add(1);
        Items.Add(2);

        // [WHEN] Sort then Reverse
        Helper.SortIntegerList(Items);
        Helper.ReverseIntegerList(Items);

        // [THEN] Elements in descending order [3, 2, 1]
        Assert.AreEqual(3, Items.Get(1), 'Sort+Reverse: first must be 3');
        Assert.AreEqual(2, Items.Get(2), 'Sort+Reverse: second must be 2');
        Assert.AreEqual(1, Items.Get(3), 'Sort+Reverse: third must be 1');
    end;
}
