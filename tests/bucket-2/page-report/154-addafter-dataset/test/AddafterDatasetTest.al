codeunit 59653 "ADDS Addafter DS Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "ADDS Helper";

    [Test]
    procedure ReportExtAddafterDataset_CompilesAndHelperRuns()
    begin
        // Positive: the compilation unit containing reportextensions that modify
        // dataset dataitems + add columns must compile, and codeunits defined
        // alongside must be callable. This is what issue #510 says currently fails.
        Assert.AreEqual('addafter-dataset', Helper.GetLabel(),
            'Helper.GetLabel must return addafter-dataset');
    end;

    [Test]
    procedure ReportExtAddafterDataset_LineTotal()
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(50, Helper.LineTotal(5, 10), 'LineTotal(5,10) must be 50');
        Assert.AreEqual(0, Helper.LineTotal(0, 99), 'LineTotal(0,99) must be 0');
        Assert.AreEqual(-15, Helper.LineTotal(3, -5), 'LineTotal(3,-5) must be -15');
    end;

    [Test]
    procedure ReportExtAddafterDataset_LineTotal_NotJustSum()
    begin
        // Negative: guard against a no-op returning qty+price.
        Assert.AreNotEqual(15, Helper.LineTotal(5, 10), 'LineTotal must not just return qty+price');
    end;

    [Test]
    procedure ReportExtAddafterDataset_Quote()
    begin
        // Proving Quote runs in same compilation unit as reportextensions.
        Assert.AreEqual('"hi"', Helper.Quote('hi'), 'Quote must wrap with double quotes');
        Assert.AreEqual('""', Helper.Quote(''), 'Quote of empty must be just quotes');
    end;

    [Test]
    procedure ReportExtAddafterDataset_Quote_NotIdentity()
    begin
        // Negative: Quote must NOT return the raw string.
        Assert.AreNotEqual('hi', Helper.Quote('hi'), 'Quote must not return the raw string');
    end;

    [Test]
    procedure ReportExtAddafterDataset_TableInCompilationUnit_Usable()
    var
        Item: Record "ADDS Item";
    begin
        // Positive: the source table must be usable — proves the whole
        // compilation unit is live.
        Item.Init();
        Item."No." := 'I1';
        Item.Description := 'Widget';
        Item.Qty := 3;
        Item.Price := 9.99;
        Item.Insert();

        Item.Reset();
        Assert.IsTrue(Item.Get('I1'), 'Item I1 must be retrievable after Insert');
        Assert.AreEqual('Widget', Item.Description, 'Description must roundtrip');
        Assert.AreEqual(3, Item.Qty, 'Qty must roundtrip');
    end;
}
