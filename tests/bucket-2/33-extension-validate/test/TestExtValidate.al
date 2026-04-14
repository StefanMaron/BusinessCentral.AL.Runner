codeunit 53300 "Test Ext Validate"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ExtFieldValidateTriggerFires()
    var
        Item: Record "Base Item Table";
    begin
        // Positive: setting extension field with validate fires OnValidate
        Item.Init();
        Item."No." := 'ITEM1';
        Item.Validate("Custom Cost", 150);
        Assert.AreEqual(Item."Category"::Premium, Item."Category", 'Category should be Premium for cost > 100');
    end;

    [Test]
    procedure ExtFieldValidateLowCost()
    var
        Item: Record "Base Item Table";
    begin
        // Positive: low cost sets Standard
        Item.Init();
        Item."No." := 'ITEM2';
        Item.Validate("Custom Cost", 50);
        Assert.AreEqual(Item."Category"::Standard, Item."Category", 'Category should be Standard for cost <= 100');
    end;

    [Test]
    procedure ExtFieldWithoutValidateNoTrigger()
    var
        Item: Record "Base Item Table";
    begin
        // Negative: direct assignment doesn't fire trigger
        Item.Init();
        Item."No." := 'ITEM3';
        Item."Custom Cost" := 200;
        Assert.AreEqual(Item."Category"::None, Item."Category", 'Category should remain None without Validate');
    end;
}
