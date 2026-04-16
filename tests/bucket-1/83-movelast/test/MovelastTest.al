codeunit 50847 "MLT Movelast Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtMovelast_CompilesAndHelperRuns()
    var
        Helper: Codeunit "MLT Product Helper";
    begin
        // Positive: the compilation unit containing a pageextension with `movelast` in
        // the `layout` area must compile, and codeunits defined alongside must be callable.
        Assert.AreEqual('movelast ok', Helper.GetLabel(), 'Helper.GetLabel must return movelast ok');
    end;

    [Test]
    procedure PageExtMovelast_AddWithBonus()
    var
        Helper: Codeunit "MLT Product Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
        Assert.AreEqual(-4, Helper.AddWithBonus(-2, -3), 'AddWithBonus(-2,-3) must return -5+1=-4');
    end;

    [Test]
    procedure PageExtMovelast_AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "MLT Product Helper";
    begin
        // Negative: AddWithBonus must NOT return a plain sum (no-op trap guard).
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;

    [Test]
    procedure PageExtMovelast_Concat()
    var
        Helper: Codeunit "MLT Product Helper";
    begin
        // Proving concat logic runs in same compilation unit as pageextensions.
        Assert.AreEqual('a|b', Helper.Concat('a', 'b'), 'Concat must join with | separator');
        Assert.AreEqual('|', Helper.Concat('', ''), 'Concat of empties must still include separator');
    end;

    [Test]
    procedure PageExtMovelast_Concat_SeparatorPresent()
    var
        Helper: Codeunit "MLT Product Helper";
    begin
        // Negative: Concat must NOT simply return a+b (missing | separator).
        Assert.AreNotEqual('ab', Helper.Concat('a', 'b'), 'Concat must not just return a+b without separator');
    end;

    [Test]
    procedure PageExtMovelast_TableInCompilationUnit_Usable()
    var
        Product: Record "MLT Product";
    begin
        // Positive: the source table in the same compilation unit as the pageextensions
        // must be usable — inserts/finds work, proving the compilation unit is live.
        Product.Init();
        Product.Code := 'SKU1';
        Product.Description := 'Widget';
        Product.Price := 9.99;
        Product.Insert();

        Product.Reset();
        Assert.IsTrue(Product.Get('SKU1'), 'Product SKU1 must be retrievable after Insert');
        Assert.AreEqual('Widget', Product.Description, 'Description must roundtrip through the table');
    end;
}
