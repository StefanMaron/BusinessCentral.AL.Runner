codeunit 50988 "ALA Addlast Action Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtAddlastAction_CompilesAndHelperRuns()
    var
        Helper: Codeunit "ALA Product Helper";
    begin
        // Positive: the compilation unit containing pageextensions with `addlast` in
        // the `actions` area must compile, and codeunits defined alongside them must
        // be callable. This is what issue #415 says currently fails.
        Assert.AreEqual('LastAction', Helper.GetLabel(), 'Helper.GetLabel must return LastAction');
    end;

    [Test]
    procedure PageExtAddlastAction_MultiplyPlusOne()
    var
        Helper: Codeunit "ALA Product Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(13, Helper.MultiplyPlusOne(3, 4), 'MultiplyPlusOne(3,4) must return 3*4+1=13');
        Assert.AreEqual(1, Helper.MultiplyPlusOne(0, 0), 'MultiplyPlusOne(0,0) must return 0*0+1=1');
        Assert.AreEqual(-5, Helper.MultiplyPlusOne(2, -3), 'MultiplyPlusOne(2,-3) must return 2*-3+1=-5');
    end;

    [Test]
    procedure PageExtAddlastAction_MultiplyPlusOne_NotPlainSum()
    var
        Helper: Codeunit "ALA Product Helper";
    begin
        // Negative: guard against the no-op trap — MultiplyPlusOne must NOT just add a+b.
        Assert.AreNotEqual(7, Helper.MultiplyPlusOne(3, 4), 'MultiplyPlusOne must not just return a+b');
    end;

    [Test]
    procedure PageExtAddlastAction_Wrap()
    var
        Helper: Codeunit "ALA Product Helper";
    begin
        // Proving Wrap runs in same compilation unit as pageextensions.
        Assert.AreEqual('[hello]', Helper.Wrap('hello'), 'Wrap must add brackets');
        Assert.AreEqual('[]', Helper.Wrap(''), 'Wrap of empty must be just brackets');
    end;

    [Test]
    procedure PageExtAddlastAction_Wrap_BracketsPresent()
    var
        Helper: Codeunit "ALA Product Helper";
    begin
        // Negative: Wrap must NOT simply return s (no brackets would be the no-op trap).
        Assert.AreNotEqual('hello', Helper.Wrap('hello'), 'Wrap must not return the raw string');
    end;

    [Test]
    procedure PageExtAddlastAction_TableInCompilationUnit_Usable()
    var
        Product: Record "ALA Product";
    begin
        // Positive: the source table in the same compilation unit as the pageextensions
        // must be usable — insert and get work, proving the compilation unit is live.
        Product.Init();
        Product.Code := 'SKU1';
        Product.Description := 'Widget';
        Product.Insert();

        Product.Reset();
        Assert.IsTrue(Product.Get('SKU1'), 'Product SKU1 must be retrievable after Insert');
        Assert.AreEqual('Widget', Product.Description, 'Description must roundtrip through the table');
    end;
}
