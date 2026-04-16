codeunit 59343 "ADM Add Dataset Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "ADM Helper";

    [Test]
    procedure ReportExtAddDataset_CompilesAndHelperRuns()
    begin
        // Positive: the compilation unit containing reportextensions with `add()`
        // in the dataset section must compile, and codeunits defined alongside
        // them must be callable. This is what issue #449 says currently fails.
        Assert.AreEqual('dataset-add', Helper.GetLabel(),
            'Helper.GetLabel must return dataset-add');
    end;

    [Test]
    procedure ReportExtAddDataset_LineTotal()
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(50, Helper.LineTotal(5, 10), 'LineTotal(5,10) must be 50');
        Assert.AreEqual(0, Helper.LineTotal(0, 99), 'LineTotal(0,99) must be 0');
        Assert.AreEqual(-20, Helper.LineTotal(-2, 10), 'LineTotal(-2,10) must be -20');
    end;

    [Test]
    procedure ReportExtAddDataset_LineTotal_NotJustSum()
    begin
        // Negative: guard against a no-op returning qty+price.
        Assert.AreNotEqual(15, Helper.LineTotal(5, 10), 'LineTotal must not just return qty+price');
    end;

    [Test]
    procedure ReportExtAddDataset_Brackets()
    begin
        // Proving Brackets runs in same compilation unit as reportextensions.
        Assert.AreEqual('{hi}', Helper.Brackets('hi'), 'Brackets must wrap with {}');
        Assert.AreEqual('{}', Helper.Brackets(''), 'Brackets of empty must be just {}');
    end;

    [Test]
    procedure ReportExtAddDataset_Brackets_BracesPresent()
    begin
        // Negative: Brackets must NOT return the raw string.
        Assert.AreNotEqual('hi', Helper.Brackets('hi'), 'Brackets must not return the raw string');
    end;

    [Test]
    procedure ReportExtAddDataset_TableInCompilationUnit_Usable()
    var
        Item: Record "ADM Item";
    begin
        // Positive: the source table in the same compilation unit as the
        // reportextensions must be usable — proves the whole compilation unit is live.
        Item.Init();
        Item."No." := 'I1';
        Item.Name := 'Widget';
        Item.Qty := 3;
        Item.Price := 9.99;
        Item.Insert();

        Item.Reset();
        Assert.IsTrue(Item.Get('I1'), 'Item I1 must be retrievable after Insert');
        Assert.AreEqual('Widget', Item.Name, 'Name must roundtrip');
        Assert.AreEqual(3, Item.Qty, 'Qty must roundtrip');
        Assert.AreEqual(9.99, Item.Price, 'Price must roundtrip');
    end;
}
