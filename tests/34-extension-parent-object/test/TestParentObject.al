codeunit 53400 "Test Parent Object"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ExtValidateCascadesToBaseField()
    var
        Rec: Record "Parent Object Table";
    begin
        // Positive: validating extension field cascades to base Category field
        Rec.Init();
        Rec."No." := 'P001';
        Rec.Validate("Ext Cost", 600);
        // Ext Cost > 500 → Category = High → Base Amount = 1000
        Assert.AreEqual(Rec.Category::High, Rec.Category, 'Category should be High');
        Assert.AreEqual(1000, Rec."Base Amount", 'Base Amount should be 1000');
    end;

    [Test]
    procedure ExtValidateLowCostCascade()
    var
        Rec: Record "Parent Object Table";
    begin
        // Positive: low ext cost cascades to Low category
        Rec.Init();
        Rec."No." := 'P002';
        Rec.Validate("Ext Cost", 200);
        Assert.AreEqual(Rec.Category::Low, Rec.Category, 'Category should be Low');
        Assert.AreEqual(100, Rec."Base Amount", 'Base Amount should be 100');
    end;

    [Test]
    procedure DirectExtAssignNoCascade()
    var
        Rec: Record "Parent Object Table";
    begin
        // Negative: direct assignment doesn't trigger validate cascade
        Rec.Init();
        Rec."No." := 'P003';
        Rec."Ext Cost" := 999;
        Assert.AreEqual(Rec.Category::None, Rec.Category, 'Category should stay None');
        Assert.AreEqual(0, Rec."Base Amount", 'Base Amount should stay 0');
    end;
}
