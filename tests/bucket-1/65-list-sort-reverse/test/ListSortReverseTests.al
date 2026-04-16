codeunit 58001 "LSR List Reverse Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "LSR List Helper";

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
        Assert.AreEqual(8, Items.Get(2), 'Double-reverse: middle must be 8');
        Assert.AreEqual(9, Items.Get(3), 'Double-reverse: last must be 9');
    end;

    [Test]
    procedure Reverse_SingleElement_NoChange()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A single-element list
        Items.Add(42);

        // [WHEN] Reverse() called
        Helper.ReverseIntegerList(Items);

        // [THEN] Still has 1 element with value 42
        Assert.AreEqual(1, Items.Count(), 'Count must remain 1');
        Assert.AreEqual(42, Items.Get(1), 'Value must remain 42');
    end;

    [Test]
    procedure Reverse_FourElements_CorrectOrder()
    var
        Items: List of [Integer];
    begin
        // [GIVEN] A list [10, 20, 30, 40]
        Items.Add(10);
        Items.Add(20);
        Items.Add(30);
        Items.Add(40);

        // [WHEN] Reverse() called
        Helper.ReverseIntegerList(Items);

        // [THEN] Reversed to [40, 30, 20, 10]
        Assert.AreEqual(40, Items.Get(1), 'First must be 40');
        Assert.AreEqual(30, Items.Get(2), 'Second must be 30');
        Assert.AreEqual(20, Items.Get(3), 'Third must be 20');
        Assert.AreEqual(10, Items.Get(4), 'Fourth must be 10');
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
        Assert.AreEqual('B', Items.Get(2), 'Middle text after Reverse must be B');
        Assert.AreEqual('A', Items.Get(3), 'Last text after Reverse must be A');
    end;

    [Test]
    procedure Reverse_Text_PreservesContent()
    var
        Items: List of [Text];
    begin
        // [GIVEN] Specific text values
        Items.Add('Alpha');
        Items.Add('Beta');
        Items.Add('Gamma');

        // [WHEN] Reverse() called
        Helper.ReverseTextList(Items);

        // [THEN] All elements still present, just reversed
        Assert.IsTrue(Items.Contains('Alpha'), 'Alpha must still be present after Reverse');
        Assert.IsTrue(Items.Contains('Gamma'), 'Gamma must still be present after Reverse');
        Assert.AreEqual('Gamma', Items.Get(1), 'Gamma must be first after Reverse');
        Assert.AreEqual('Alpha', Items.Get(3), 'Alpha must be last after Reverse');
    end;
}
