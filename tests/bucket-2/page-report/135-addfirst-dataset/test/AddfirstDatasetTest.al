codeunit 59302 "AFDS Addfirst Dataset Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportExtAddfirstDataset_CompilesAndHelperRuns()
    var
        Helper: Codeunit "AFDS Helper";
    begin
        // Positive: the compilation unit containing reportextensions with `addfirst` in
        // the `dataset` area must compile, and codeunits defined alongside them must
        // be callable. This is what issue #416 says currently fails.
        Assert.AreEqual('dataset first', Helper.GetLabel(), 'Helper.GetLabel must return dataset first');
    end;

    [Test]
    procedure ReportExtAddfirstDataset_AddWithBonus()
    var
        Helper: Codeunit "AFDS Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
        Assert.AreEqual(-4, Helper.AddWithBonus(-2, -3), 'AddWithBonus(-2,-3) must return -5+1=-4');
    end;

    [Test]
    procedure ReportExtAddfirstDataset_AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "AFDS Helper";
    begin
        // Negative: guard against the no-op trap — AddWithBonus must NOT return a plain sum.
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;

    [Test]
    procedure ReportExtAddfirstDataset_Concat()
    var
        Helper: Codeunit "AFDS Helper";
    begin
        // Proving concat logic runs in same compilation unit as reportextensions.
        Assert.AreEqual('a|b', Helper.Concat('a', 'b'), 'Concat must join with | separator');
        Assert.AreEqual('|', Helper.Concat('', ''), 'Concat of empties must still include separator');
    end;

    [Test]
    procedure ReportExtAddfirstDataset_Concat_SeparatorPresent()
    var
        Helper: Codeunit "AFDS Helper";
    begin
        // Negative: Concat must NOT simply return a+b (which would miss the '|' separator).
        Assert.AreNotEqual('ab', Helper.Concat('a', 'b'), 'Concat must not just return a+b without separator');
    end;

    [Test]
    procedure ReportExtAddfirstDataset_TableInCompilationUnit_Usable()
    var
        Item: Record "AFDS Item";
    begin
        // Positive: the source table in the same compilation unit as the reportextensions
        // must be usable — inserts/finds work, proving the compilation unit is live.
        Item.Init();
        Item.Code := 'ITEM1';
        Item.Description := 'Test Item';
        Item.Quantity := 5;
        Item.Insert();

        Item.Reset();
        Assert.IsTrue(Item.Get('ITEM1'), 'ITEM1 must be retrievable after Insert');
        Assert.AreEqual('Test Item', Item.Description, 'Description must roundtrip through the table');
        Assert.AreEqual(5, Item.Quantity, 'Quantity must roundtrip through the table');
    end;
}
