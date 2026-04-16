codeunit 59602 "ABDS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ReportExtAddbefore_CompilesAndHelperRuns()
    var
        Helper: Codeunit "ABDS Helper";
    begin
        // Positive: the compilation unit containing reportextensions with `addbefore` in
        // the `dataset` area must compile, and codeunits defined alongside them must
        // be callable. This is what issue #421 says currently fails.
        Assert.AreEqual('Addbefore Dataset Helper', Helper.GetLabel(), 'Helper.GetLabel must return Addbefore Dataset Helper');
    end;

    [Test]
    procedure ReportExtAddbefore_AddWithBonus()
    var
        Helper: Codeunit "ABDS Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(17, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+10=17');
        Assert.AreEqual(10, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+10=10');
        Assert.AreEqual(5, Helper.AddWithBonus(-2, -3), 'AddWithBonus(-2,-3) must return -5+10=5');
    end;

    [Test]
    procedure ReportExtAddbefore_AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "ABDS Helper";
    begin
        // Negative: guard against the no-op trap — AddWithBonus must NOT return a plain sum.
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;

    [Test]
    procedure ReportExtAddbefore_Concat()
    var
        Helper: Codeunit "ABDS Helper";
    begin
        // Proving concat logic runs in same compilation unit as reportextensions.
        Assert.AreEqual('a:b', Helper.Concat('a', 'b'), 'Concat must join with : separator');
        Assert.AreEqual(':', Helper.Concat('', ''), 'Concat of empties must still include separator');
    end;

    [Test]
    procedure ReportExtAddbefore_Concat_SeparatorPresent()
    var
        Helper: Codeunit "ABDS Helper";
    begin
        // Negative: Concat must NOT simply return a+b (which would miss the ':' separator).
        Assert.AreNotEqual('ab', Helper.Concat('a', 'b'), 'Concat must not just return a+b without separator');
    end;

    [Test]
    procedure ReportExtAddbefore_TableInCompilationUnit_Usable()
    var
        Item: Record "ABDS Item";
    begin
        // Positive: the source table in the same compilation unit as the reportextensions
        // must be usable — inserts/finds work, proving the compilation unit is live.
        Item.Init();
        Item."No." := 'ITEM1';
        Item.Name := 'Test Item';
        Item.Qty := 42;
        Item.Insert();

        Item.Reset();
        Assert.IsTrue(Item.Get('ITEM1'), 'ITEM1 must be retrievable after Insert');
        Assert.AreEqual('Test Item', Item.Name, 'Name must roundtrip through the table');
        Assert.AreEqual(42, Item.Qty, 'Qty must roundtrip through the table');
    end;
}
