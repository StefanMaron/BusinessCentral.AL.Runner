codeunit 50139 "ALFG Addlast Fieldgroup Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure TableExtAddlastFieldgroup_CompilesAndHelperRuns()
    var
        Helper: Codeunit "ALFG Helper";
    begin
        // Positive: the compilation unit containing a tableextension with `addlast`
        // in the `fieldgroups` section must compile, and codeunits defined alongside
        // must be callable. This is what issue #443 says currently fails.
        Assert.AreEqual('fieldgroup last', Helper.GetLabel(), 'Helper.GetLabel must return fieldgroup last');
    end;

    [Test]
    procedure TableExtAddlastFieldgroup_AddWithBonus()
    var
        Helper: Codeunit "ALFG Helper";
    begin
        // Proving: helper performs real work — a+b+1, not a no-op (issue #203 standard).
        Assert.AreEqual(8, Helper.AddWithBonus(3, 4), 'AddWithBonus(3,4) must return 3+4+1=8');
        Assert.AreEqual(1, Helper.AddWithBonus(0, 0), 'AddWithBonus(0,0) must return 0+0+1=1');
        Assert.AreEqual(-4, Helper.AddWithBonus(-2, -3), 'AddWithBonus(-2,-3) must return -5+1=-4');
    end;

    [Test]
    procedure TableExtAddlastFieldgroup_AddWithBonus_NotPlainSum()
    var
        Helper: Codeunit "ALFG Helper";
    begin
        // Negative: guard against no-op trap — AddWithBonus must include the +1 bonus.
        Assert.AreNotEqual(7, Helper.AddWithBonus(3, 4), 'AddWithBonus must not just return a+b');
    end;

    [Test]
    procedure TableExtAddlastFieldgroup_Concat()
    var
        Helper: Codeunit "ALFG Helper";
    begin
        // Proving concat logic runs in same compilation unit as the tableextension.
        Assert.AreEqual('x|y', Helper.Concat('x', 'y'), 'Concat must join with | separator');
        Assert.AreEqual('|', Helper.Concat('', ''), 'Concat of empties must still include separator');
    end;

    [Test]
    procedure TableExtAddlastFieldgroup_Concat_SeparatorPresent()
    var
        Helper: Codeunit "ALFG Helper";
    begin
        // Negative: Concat must NOT simply return a+b without the separator.
        Assert.AreNotEqual('xy', Helper.Concat('x', 'y'), 'Concat must not return a+b without separator');
    end;

    [Test]
    procedure TableExtAddlastFieldgroup_TableUsable()
    var
        Item: Record "ALFG Item";
    begin
        // Positive: the extended table must be fully usable alongside the tableextension.
        Item.Init();
        Item."No." := 'ITEM1';
        Item.Name := 'Widget';
        Item.Description := 'A small widget';
        Item.Quantity := 10;
        Item.Insert();

        Item.Reset();
        Assert.IsTrue(Item.Get('ITEM1'), 'ITEM1 must be retrievable after Insert');
        Assert.AreEqual('Widget', Item.Name, 'Name must roundtrip through the table');
        Assert.AreEqual(10, Item.Quantity, 'Quantity must roundtrip through the table');
    end;
}
