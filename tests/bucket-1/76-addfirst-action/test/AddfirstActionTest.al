codeunit 50798 "AFA Addfirst Action Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtAddfirstAction_CompilesAndHelperRuns()
    var
        Helper: Codeunit "AFA Product Helper";
    begin
        // Positive: the compilation unit containing pageextensions with `addfirst` in
        // the `actions` area must compile, and codeunits defined alongside them must
        // be callable. This is what issue #413 says currently fails.
        Assert.AreEqual('first', Helper.GetMessage(), 'Helper.GetMessage must return first');
    end;

    [Test]
    procedure PageExtAddfirstAction_AddWithBonus()
    var
        Helper: Codeunit "AFA Product Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
        Assert.AreEqual(-4, Helper.AddWithBonus(-2, -3), 'AddWithBonus(-2,-3) must return -5+1=-4');
    end;

    [Test]
    procedure PageExtAddfirstAction_AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "AFA Product Helper";
    begin
        // Negative: guard against the no-op trap — AddWithBonus must NOT return a plain sum.
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;

    [Test]
    procedure PageExtAddfirstAction_Concat()
    var
        Helper: Codeunit "AFA Product Helper";
    begin
        // Proving concat logic runs in same compilation unit as pageextensions.
        Assert.AreEqual('a|b', Helper.Concat('a', 'b'), 'Concat must join with | separator');
        Assert.AreEqual('|', Helper.Concat('', ''), 'Concat of empties must still include separator');
    end;

    [Test]
    procedure PageExtAddfirstAction_Concat_SeparatorPresent()
    var
        Helper: Codeunit "AFA Product Helper";
    begin
        // Negative: Concat must NOT simply return a+b (which would miss the '|' separator).
        Assert.AreNotEqual('ab', Helper.Concat('a', 'b'), 'Concat must not just return a+b without separator');
    end;

    [Test]
    procedure PageExtAddfirstAction_TableInCompilationUnit_Usable()
    var
        Product: Record "AFA Product";
    begin
        // Positive: the source table in the same compilation unit as the pageextensions
        // must be usable — inserts/finds work, proving the compilation unit is live.
        Product.Init();
        Product.Code := 'SKU1';
        Product.Description := 'Widget';
        Product.Insert();

        Product.Reset();
        Assert.IsTrue(Product.Get('SKU1'), 'Product SKU1 must be retrievable after Insert');
        Assert.AreEqual('Widget', Product.Description, 'Description must roundtrip through the table');
    end;
}
