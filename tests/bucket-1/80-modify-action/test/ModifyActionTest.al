codeunit 50980 "MAM Modify Action Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtModifyAction_CompilesAndHelperRuns()
    var
        Helper: Codeunit "MAM Product Helper";
    begin
        // Positive: the compilation unit containing pageextensions with `modify` in
        // the `actions` area must compile, and codeunits defined alongside them must
        // be callable. This is what issue #422 says currently fails.
        Assert.AreEqual('Modified Caption', Helper.GetActionCaption(),
            'Helper.GetActionCaption must return Modified Caption');
    end;

    [Test]
    procedure PageExtModifyAction_SquarePlusTwo()
    var
        Helper: Codeunit "MAM Product Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(11, Helper.SquarePlusTwo(3), 'SquarePlusTwo(3) must return 3*3+2=11');
        Assert.AreEqual(2, Helper.SquarePlusTwo(0), 'SquarePlusTwo(0) must return 0*0+2=2');
        Assert.AreEqual(27, Helper.SquarePlusTwo(5), 'SquarePlusTwo(5) must return 5*5+2=27');
    end;

    [Test]
    procedure PageExtModifyAction_SquarePlusTwo_NotJustSquare()
    var
        Helper: Codeunit "MAM Product Helper";
    begin
        // Negative: guard against the no-op trap — SquarePlusTwo must NOT just square.
        Assert.AreNotEqual(9, Helper.SquarePlusTwo(3), 'SquarePlusTwo must not just return n*n');
    end;

    [Test]
    procedure PageExtModifyAction_Quote()
    var
        Helper: Codeunit "MAM Product Helper";
    begin
        // Proving Quote runs in the same compilation unit as pageextensions.
        Assert.AreEqual('"hi"', Helper.Quote('hi'), 'Quote must wrap in double quotes');
        Assert.AreEqual('""', Helper.Quote(''), 'Quote of empty must be just two quotes');
    end;

    [Test]
    procedure PageExtModifyAction_Quote_QuotesPresent()
    var
        Helper: Codeunit "MAM Product Helper";
    begin
        // Negative: Quote must NOT simply return s (missing quotes = no-op trap).
        Assert.AreNotEqual('hi', Helper.Quote('hi'), 'Quote must not return the raw string');
    end;

    [Test]
    procedure PageExtModifyAction_TableInCompilationUnit_Usable()
    var
        Product: Record "MAM Product";
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
