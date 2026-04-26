codeunit 50240 "SetRange Clear Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    local procedure Seed()
    var
        Item: Record "SR Item";
    begin
        Item.DeleteAll();
        Item.Init(); Item."No." := 'A'; Item.Status := 1; Item.Category := 10; Item.Insert();
        Item.Init(); Item."No." := 'B'; Item.Status := 2; Item.Category := 10; Item.Insert();
        Item.Init(); Item."No." := 'C'; Item.Status := 1; Item.Category := 20; Item.Insert();
        Item.Init(); Item."No." := 'D'; Item.Status := 3; Item.Category := 20; Item.Insert();
    end;

    [Test]
    procedure SetRange_Clears_Filter_On_Field()
    var
        Item: Record "SR Item";
    begin
        Seed();

        // [GIVEN] Filter on Status = 1
        Item.SetRange(Status, 1);
        Assert.AreEqual(2, Item.Count(), 'Status=1 should match 2 records (A, C)');

        // [WHEN] SetRange(Status) called with no value
        Item.SetRange(Status);

        // [THEN] Filter cleared — all 4 records returned
        Assert.AreEqual(4, Item.Count(), 'SetRange(Status) with no value must clear the filter — expected all 4 records');
    end;

    [Test]
    procedure SetRange_Clears_Only_Target_Field()
    var
        Item: Record "SR Item";
    begin
        Seed();

        // [GIVEN] Filters on two fields
        Item.SetRange(Status, 1);
        Item.SetRange(Category, 10);
        Assert.AreEqual(1, Item.Count(), 'Status=1 AND Category=10 should match only A');

        // [WHEN] Clear only the Status filter
        Item.SetRange(Status);

        // [THEN] Category filter is still active — A and B match (Category=10)
        Assert.AreEqual(2, Item.Count(), 'After clearing Status filter, Category=10 filter must remain — expected 2 records');

        // [AND] Narrow again with a fresh Status filter to prove filters can be reapplied
        Item.SetRange(Status, 2);
        Assert.AreEqual(1, Item.Count(), 'Status=2 AND Category=10 should match only B');
    end;

    [Test]
    procedure SetRange_Clear_On_Unfiltered_Field_Is_Noop()
    var
        Item: Record "SR Item";
    begin
        Seed();
        // [GIVEN] No filter on Category
        Item.SetRange(Status, 1);

        // [WHEN] Clear filter on Category (which had no filter)
        Item.SetRange(Category);

        // [THEN] Status filter unchanged — still 2 records
        Assert.AreEqual(2, Item.Count(), 'Clearing unfiltered field must not affect other filters');
    end;

    [Test]
    procedure SetRange_Clear_Then_FindSet_Iterates_All()
    var
        Item: Record "SR Item";
        Visited: Integer;
    begin
        Seed();
        Item.SetRange(Status, 1);
        Item.SetRange(Status); // clear

        Visited := 0;
        if Item.FindSet() then
            repeat
                Visited += 1;
            until Item.Next() = 0;

        Assert.AreEqual(4, Visited, 'FindSet after cleared filter must iterate all 4 records');
    end;
}
