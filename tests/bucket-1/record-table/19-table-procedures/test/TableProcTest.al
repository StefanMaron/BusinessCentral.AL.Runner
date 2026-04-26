codeunit 50919 "Table Procedure Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TestGetDisplayName()
    var
        Prod: Record "Product";
    begin
        // [GIVEN] A product with first and last name
        Prod.Init();
        Prod."Code" := 'P001';
        Prod."First Name" := 'John';
        Prod."Last Name" := 'Doe';
        Prod."Quantity" := 0;
        Prod."Limit" := 100;
        Prod.Insert(true);

        // [WHEN] Calling GetDisplayName
        Prod.Get('P001');

        // [THEN] It should return the full name
        Assert.AreEqual('John Doe', Prod.GetDisplayName(), 'Display name should combine first and last');
    end;

    [Test]
    procedure TestIsOverLimit_False()
    var
        Prod: Record "Product";
    begin
        Prod.Init();
        Prod."Code" := 'P002';
        Prod."First Name" := 'Jane';
        Prod."Last Name" := 'Smith';
        Prod."Quantity" := 5;
        Prod."Limit" := 10;
        Prod.Insert(true);

        Prod.Get('P002');
        Assert.IsFalse(Prod.IsOverLimit(), 'Should not be over limit when Qty < Limit');
    end;

    [Test]
    procedure TestIsOverLimit_True()
    var
        Prod: Record "Product";
    begin
        Prod.Init();
        Prod."Code" := 'P003';
        Prod."First Name" := 'Bob';
        Prod."Last Name" := 'Jones';
        Prod."Quantity" := 15;
        Prod."Limit" := 10;
        Prod.Insert(true);

        Prod.Get('P003');
        Assert.IsTrue(Prod.IsOverLimit(), 'Should be over limit when Qty > Limit');
    end;

    [Test]
    procedure TestRemainingCapacity()
    var
        Prod: Record "Product";
    begin
        Prod.Init();
        Prod."Code" := 'P004';
        Prod."First Name" := 'Alice';
        Prod."Last Name" := 'Brown';
        Prod."Quantity" := 7;
        Prod."Limit" := 10;
        Prod.Insert(true);

        Prod.Get('P004');
        Assert.AreEqual(3, Prod.RemainingCapacity(), 'Remaining should be Limit - Quantity');
    end;

    [Test]
    procedure TestRemainingCapacityAtLimit()
    var
        Prod: Record "Product";
    begin
        Prod.Init();
        Prod."Code" := 'P005';
        Prod."First Name" := 'Eve';
        Prod."Last Name" := 'White';
        Prod."Quantity" := 10;
        Prod."Limit" := 10;
        Prod.Insert(true);

        Prod.Get('P005');
        Assert.AreEqual(0, Prod.RemainingCapacity(), 'Remaining should be 0 at limit');
    end;

    [Test]
    procedure TestIsOverLimit_ZeroLimit()
    var
        Prod: Record "Product";
    begin
        // [GIVEN] A product with zero limit and positive quantity
        Prod.Init();
        Prod."Code" := 'P006';
        Prod."First Name" := 'Zero';
        Prod."Last Name" := 'Limit';
        Prod."Quantity" := 1;
        Prod."Limit" := 0;
        Prod.Insert(true);

        // [WHEN] Checking if over limit
        Prod.Get('P006');

        // [THEN] Should be over limit (1 > 0)
        Assert.IsTrue(Prod.IsOverLimit(), 'Quantity 1 with Limit 0 should be over limit');
    end;

    [Test]
    procedure TestRemainingCapacity_ZeroLimit()
    var
        Prod: Record "Product";
    begin
        // [GIVEN] A product with zero limit
        Prod.Init();
        Prod."Code" := 'P007';
        Prod."First Name" := 'No';
        Prod."Last Name" := 'Cap';
        Prod."Quantity" := 5;
        Prod."Limit" := 0;
        Prod.Insert(true);

        // [WHEN] Checking remaining capacity
        Prod.Get('P007');

        // [THEN] Should be 0 (at/over limit)
        Assert.AreEqual(0, Prod.RemainingCapacity(), 'Remaining should be 0 when over limit');
    end;

    [Test]
    procedure TestIsOverLimit_EqualToLimit()
    var
        Prod: Record "Product";
    begin
        // [GIVEN] A product where quantity exactly equals limit
        Prod.Init();
        Prod."Code" := 'P008';
        Prod."First Name" := 'At';
        Prod."Last Name" := 'Limit';
        Prod."Quantity" := 10;
        Prod."Limit" := 10;
        Prod.Insert(true);

        // [WHEN] Checking if over limit
        Prod.Get('P008');

        // [THEN] Should NOT be over limit (10 > 10 is false)
        Assert.IsFalse(Prod.IsOverLimit(), 'Quantity equal to Limit should not be over limit');
    end;
}
