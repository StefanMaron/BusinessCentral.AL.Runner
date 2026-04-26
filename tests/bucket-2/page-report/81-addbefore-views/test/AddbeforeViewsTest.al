codeunit 50981 "ABV Addbefore Views Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtAddbeforeViews_CompilesAndHelperRuns()
    var
        Helper: Codeunit "ABV Product Helper";
    begin
        // Positive: the compilation unit containing pageextensions with `addbefore`
        // in the `views` area must compile, and codeunits defined alongside them
        // must be callable. This is what issue #424 says currently fails.
        Assert.AreEqual('BeforeView', Helper.GetViewLabel(),
            'Helper.GetViewLabel must return BeforeView');
    end;

    [Test]
    procedure PageExtAddbeforeViews_DoublePlusThree()
    var
        Helper: Codeunit "ABV Product Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(13, Helper.DoublePlusThree(5), 'DoublePlusThree(5) must return 5*2+3=13');
        Assert.AreEqual(3, Helper.DoublePlusThree(0), 'DoublePlusThree(0) must return 0*2+3=3');
        Assert.AreEqual(-1, Helper.DoublePlusThree(-2), 'DoublePlusThree(-2) must return -4+3=-1');
    end;

    [Test]
    procedure PageExtAddbeforeViews_DoublePlusThree_NotJustDouble()
    var
        Helper: Codeunit "ABV Product Helper";
    begin
        // Negative: guard against the no-op trap — DoublePlusThree must NOT just double.
        Assert.AreNotEqual(10, Helper.DoublePlusThree(5), 'DoublePlusThree must not just return n*2');
    end;

    [Test]
    procedure PageExtAddbeforeViews_Tag()
    var
        Helper: Codeunit "ABV Product Helper";
    begin
        // Proving Tag runs in same compilation unit as pageextensions.
        Assert.AreEqual('<hi>', Helper.Tag('hi'), 'Tag must wrap in angle brackets');
        Assert.AreEqual('<>', Helper.Tag(''), 'Tag of empty must be just angle brackets');
    end;

    [Test]
    procedure PageExtAddbeforeViews_Tag_BracketsPresent()
    var
        Helper: Codeunit "ABV Product Helper";
    begin
        // Negative: Tag must NOT simply return s (no brackets = no-op trap).
        Assert.AreNotEqual('hi', Helper.Tag('hi'), 'Tag must not return the raw string');
    end;

    [Test]
    procedure PageExtAddbeforeViews_TableInCompilationUnit_Usable()
    var
        Product: Record "ABV Product";
    begin
        // Positive: the source table in the same compilation unit as the pageextensions
        // must be usable — insert and get work, proving the compilation unit is live.
        Product.Init();
        Product.Code := 'SKU1';
        Product.Active := true;
        Product.Insert();

        Product.Reset();
        Assert.IsTrue(Product.Get('SKU1'), 'Product SKU1 must be retrievable after Insert');
        Assert.AreEqual(true, Product.Active, 'Active must roundtrip through the table');
    end;
}
