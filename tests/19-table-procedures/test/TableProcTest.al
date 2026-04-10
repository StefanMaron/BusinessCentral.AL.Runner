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
}
