codeunit 50901 "Inventory Management Tests"
{
    Subtype = Test;

    var
        InvMgmt: Codeunit "Inventory Management";
        Assert: Codeunit "Library Assert";

    [Test]
    procedure TestAddStock()
    var
        Item: Record "Sample Item";
    begin
        // [GIVEN] An item with 10 in stock
        Item.Init();
        Item."No." := 'ITEM-001';
        Item."Description" := 'Test Widget';
        Item."Unit Price" := 25.50;
        Item."Inventory" := 10;
        Item.Insert(true);

        // [WHEN] Adding 5 units
        InvMgmt.AddStock('ITEM-001', 5);

        // [THEN] Inventory should be 15
        Item.Get('ITEM-001');
        Assert.AreEqual(15, Item."Inventory", 'Inventory should be 15 after adding 5 to 10');
    end;

    [Test]
    procedure TestRemoveStock()
    var
        Item: Record "Sample Item";
    begin
        Item.Init();
        Item."No." := 'ITEM-002';
        Item."Inventory" := 20;
        Item.Insert(true);

        InvMgmt.RemoveStock('ITEM-002', 8);

        Item.Get('ITEM-002');
        Assert.AreEqual(12, Item."Inventory", 'Inventory should be 12 after removing 8 from 20');
    end;

    [Test]
    procedure TestCalculateOrderTotal()
    var
        OrderLine: Record "Sample Order Line";
        Total: Decimal;
    begin
        // [GIVEN] Three order lines for order ORD-001
        OrderLine.Init();
        OrderLine."Order No." := 'ORD-001';
        OrderLine."Line No." := 10000;
        OrderLine."Item No." := 'ITEM-A';
        OrderLine."Quantity" := 2;
        OrderLine."Line Amount" := 50.00;
        OrderLine.Insert(true);

        OrderLine.Init();
        OrderLine."Order No." := 'ORD-001';
        OrderLine."Line No." := 20000;
        OrderLine."Item No." := 'ITEM-B';
        OrderLine."Quantity" := 1;
        OrderLine."Line Amount" := 100.00;
        OrderLine.Insert(true);

        OrderLine.Init();
        OrderLine."Order No." := 'ORD-001';
        OrderLine."Line No." := 30000;
        OrderLine."Item No." := 'ITEM-C';
        OrderLine."Quantity" := 3;
        OrderLine."Line Amount" := 30.00;
        OrderLine.Insert(true);

        // [WHEN] Calculating order total
        Total := InvMgmt.CalculateOrderTotal('ORD-001');

        // [THEN] Total should be 180
        Assert.AreEqual(180, Total, 'Order total should be 50 + 100 + 30 = 180');
    end;

    [Test]
    procedure TestSetRangeFiltering()
    var
        Item: Record "Sample Item";
        Count: Integer;
    begin
        // [GIVEN] Items with various prices
        CreateItem('CHEAP-1', 'Cheap Widget', 5.00, 100);
        CreateItem('MID-1', 'Mid Widget', 50.00, 50);
        CreateItem('PRICEY-1', 'Expensive Widget', 200.00, 10);

        // [WHEN] Filtering items with price >= 50
        Item.SetFilter("Unit Price", '>=50');

        // [THEN] Should find 2 items
        Assert.AreEqual(2, Item.Count(), 'Should find 2 items with price >= 50');
    end;

    local procedure CreateItem(ItemNo: Code[20]; Description: Text[100]; Price: Decimal; Inventory: Integer)
    var
        Item: Record "Sample Item";
    begin
        Item.Init();
        Item."No." := ItemNo;
        Item."Description" := Description;
        Item."Unit Price" := Price;
        Item."Inventory" := Inventory;
        Item.Insert(true);
    end;
}
