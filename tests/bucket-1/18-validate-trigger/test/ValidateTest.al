codeunit 50918 "Validate Trigger Tests"
{
    Subtype = Test;

    var
        ValidateHelper: Codeunit "Validate Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestNameUppercased()
    var
        Rec: Record "Validate Demo";
    begin
        // [GIVEN] An entry created with lowercase name via Validate
        ValidateHelper.CreateEntry(1, 'john doe', 1, 10);

        // [WHEN] Reading back the record
        Rec.Get(1);

        // [THEN] Name should be uppercased by OnValidate trigger
        Assert.AreEqual('JOHN DOE', Rec."Name", 'OnValidate should uppercase the name');
    end;

    [Test]
    procedure TestQuantityCalculatesAmount()
    var
        Rec: Record "Validate Demo";
    begin
        // [GIVEN] An entry with price 25 and quantity 4
        ValidateHelper.CreateEntry(2, 'Widget', 4, 25);

        // [WHEN] Reading back
        Rec.Get(2);

        // [THEN] Amount should be Quantity * Unit Price = 100
        Assert.AreEqual(100, Rec."Amount", 'Amount should be Qty * Price');
    end;

    [Test]
    procedure TestDirectValidateOnRecord()
    var
        Rec: Record "Validate Demo";
    begin
        // [GIVEN] A record inserted without Validate
        Rec.Init();
        Rec."Entry No." := 3;
        Rec."Unit Price" := 10;
        Rec."Name" := 'lowercase';
        Rec."Quantity" := 0;
        Rec."Amount" := 0;
        Rec.Insert(true);

        // [WHEN] Validating Name directly
        Rec.Get(3);
        Rec.Validate("Name", 'updated name');
        Rec.Modify();

        // [THEN] Name should be uppercased
        Rec.Get(3);
        Assert.AreEqual('UPDATED NAME', Rec."Name", 'Direct Validate should fire OnValidate');
    end;

    [Test]
    procedure TestDirectAssignSkipsValidateTrigger()
    var
        Rec: Record "Validate Demo";
    begin
        // [GIVEN] A record where Name is assigned directly (not via Validate)
        Rec.Init();
        Rec."Entry No." := 4;
        Rec."Unit Price" := 10;
        Rec."Name" := 'lower case name';
        Rec."Quantity" := 5;
        Rec."Amount" := 0;
        Rec.Insert(true);

        // [WHEN] Reading back the record
        Rec.Get(4);

        // [THEN] Name should NOT be uppercased (OnValidate was not fired)
        Assert.AreEqual('lower case name', Rec."Name", 'Direct assign should not fire OnValidate');
        // [THEN] Amount should NOT be computed (Quantity OnValidate was not fired)
        Assert.AreEqual(0, Rec."Amount", 'Direct assign of Quantity should not compute Amount');
    end;

    [Test]
    procedure TestZeroQuantityValidate()
    var
        Rec: Record "Validate Demo";
    begin
        // [GIVEN] An entry with price 50 and quantity 0
        ValidateHelper.CreateEntry(5, 'Zero Qty', 0, 50);

        // [WHEN] Reading back
        Rec.Get(5);

        // [THEN] Amount should be 0
        Assert.AreEqual(0, Rec."Amount", 'Zero quantity should produce zero amount');
        // [THEN] Name should still be uppercased
        Assert.AreEqual('ZERO QTY', Rec."Name", 'Name should be uppercased even with zero qty');
    end;
}
