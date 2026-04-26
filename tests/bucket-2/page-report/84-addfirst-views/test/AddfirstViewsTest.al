codeunit 50858 "AFV Addfirst Views Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtAddfirstViews_CompilesAndHelperRuns()
    var
        Helper: Codeunit "AFV Product Helper";
    begin
        // Positive: the compilation unit containing a pageextension with `addfirst`
        // (no anchor) in the `views` area must compile, and codeunits defined
        // alongside must be callable.
        Assert.AreEqual('FirstView', Helper.GetViewLabel(), 'Helper.GetViewLabel must return FirstView');
    end;

    [Test]
    procedure PageExtAddfirstViews_DoublePlusThree()
    var
        Helper: Codeunit "AFV Product Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(11, Helper.DoublePlusThree(4), 'DoublePlusThree(4) must return 4*2+3=11');
        Assert.AreEqual(3, Helper.DoublePlusThree(0), 'DoublePlusThree(0) must return 0*2+3=3');
        Assert.AreEqual(-1, Helper.DoublePlusThree(-2), 'DoublePlusThree(-2) must return -2*2+3=-1');
    end;

    [Test]
    procedure PageExtAddfirstViews_DoublePlusThree_NotPlainDouble()
    var
        Helper: Codeunit "AFV Product Helper";
    begin
        // Negative: DoublePlusThree must NOT return plain 2*n (no-op trap guard).
        Assert.AreNotEqual(8, Helper.DoublePlusThree(4), 'DoublePlusThree must not just return 2*n');
    end;

    [Test]
    procedure PageExtAddfirstViews_Tag()
    var
        Helper: Codeunit "AFV Product Helper";
    begin
        // Proving tag logic runs in same compilation unit as pageextensions.
        Assert.AreEqual('<hello>', Helper.Tag('hello'), 'Tag must wrap with angle brackets');
        Assert.AreEqual('<>', Helper.Tag(''), 'Tag of empty must still return <>');
    end;

    [Test]
    procedure PageExtAddfirstViews_Tag_BracketsPresent()
    var
        Helper: Codeunit "AFV Product Helper";
    begin
        // Negative: Tag must NOT simply return the input unchanged.
        Assert.AreNotEqual('hello', Helper.Tag('hello'), 'Tag must not just return the input');
    end;

    [Test]
    procedure PageExtAddfirstViews_TableInCompilationUnit_Usable()
    var
        Product: Record "AFV Product";
    begin
        // Positive: the source table in the same compilation unit as the pageextensions
        // must be usable — inserts/finds work, proving the compilation unit is live.
        Product.Init();
        Product.Code := 'P001';
        Product.Active := true;
        Product.Insert();

        Product.Reset();
        Assert.IsTrue(Product.Get('P001'), 'Product P001 must be retrievable after Insert');
        Assert.AreEqual(true, Product.Active, 'Active must roundtrip through the table');
    end;
}
