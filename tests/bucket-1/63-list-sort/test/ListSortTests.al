codeunit 81101 "List Sort Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "List Sort Helper";

    // ------------------------------------------------------------------
    // Positive: Sort() reorders integer elements ascending.
    // ------------------------------------------------------------------

    [Test]
    procedure Sort_Integer_SortsAscending()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] An unsorted list [3, 1, 2]
        Items.Add(3);
        Items.Add(1);
        Items.Add(2);

        // [WHEN] Sort() is called
        Helper.SortIntegerList(Items);

        // [THEN] Elements are in ascending order [1, 2, 3]
        Assert.AreEqual(1, Items.Get(1), 'First element after Sort must be 1');
        Assert.AreEqual(2, Items.Get(2), 'Second element after Sort must be 2');
        Assert.AreEqual(3, Items.Get(3), 'Third element after Sort must be 3');
    end;

    [Test]
    procedure Sort_Integer_PreservesCount()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A 4-element list
        Items.Add(40);
        Items.Add(10);
        Items.Add(30);
        Items.Add(20);

        // [WHEN] Sort() is called
        Helper.SortIntegerList(Items);

        // [THEN] Count is unchanged
        Assert.AreEqual(4, Items.Count(), 'Sort must not change the element count');
    end;

    [Test]
    procedure Sort_Integer_AlreadySorted_Unchanged()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] An already-sorted list [1, 2, 3]
        Items.Add(1);
        Items.Add(2);
        Items.Add(3);

        // [WHEN] Sort() is called
        Helper.SortIntegerList(Items);

        // [THEN] Elements remain in ascending order
        Assert.AreEqual(1, Items.Get(1), 'First must still be 1');
        Assert.AreEqual(2, Items.Get(2), 'Second must still be 2');
        Assert.AreEqual(3, Items.Get(3), 'Third must still be 3');
    end;

    [Test]
    procedure Sort_Integer_SingleElement_NoChange()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A single-element list
        Items.Add(42);

        // [WHEN] Sort() is called
        Helper.SortIntegerList(Items);

        // [THEN] Single element unchanged
        Assert.AreEqual(1, Items.Count(), 'Count must remain 1');
        Assert.AreEqual(42, Items.Get(1), 'Value must remain 42');
    end;

    // ------------------------------------------------------------------
    // Positive: Sort() reorders text elements lexicographically.
    // ------------------------------------------------------------------

    [Test]
    procedure Sort_Text_SortsLexicographically()
    var
        Items: List of [Text];
    begin
        // [GIVEN] An unsorted text list ['Banana', 'Apple', 'Cherry']
        Items.Add('Banana');
        Items.Add('Apple');
        Items.Add('Cherry');

        // [WHEN] Sort() is called
        Helper.SortTextList(Items);

        // [THEN] Elements are in lexicographic order
        Assert.AreEqual('Apple', Items.Get(1), 'First after Sort must be Apple');
        Assert.AreEqual('Banana', Items.Get(2), 'Second after Sort must be Banana');
        Assert.AreEqual('Cherry', Items.Get(3), 'Third after Sort must be Cherry');
    end;
}
