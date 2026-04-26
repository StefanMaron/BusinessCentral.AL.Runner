codeunit 59871 "VCA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "VCA Helper";

    [Test]
    procedure VisibleCondition_CompilesAndHelperRuns()
    begin
        // Positive: compilation unit containing a page with conditional
        // `Visible = <expression>` attributes must compile, and codeunits
        // defined alongside must remain callable.
        Assert.AreEqual('visible-conditional', Helper.GetLabel(),
            'Helper.GetLabel must return visible-conditional');
    end;

    [Test]
    procedure VisibleCondition_ShouldShowPrice_True()
    begin
        // Proving: the logic the page uses for Visible is reachable from test code.
        Assert.IsTrue(Helper.ShouldShowPrice(1.0), 'Price 1.0 must show (>0)');
        Assert.IsTrue(Helper.ShouldShowPrice(0.01), 'Price 0.01 must show (>0)');
        Assert.IsTrue(Helper.ShouldShowPrice(1000), 'Price 1000 must show');
    end;

    [Test]
    procedure VisibleCondition_ShouldShowPrice_False()
    begin
        Assert.IsFalse(Helper.ShouldShowPrice(0), 'Price 0 must not show');
        Assert.IsFalse(Helper.ShouldShowPrice(-1), 'Negative price must not show');
    end;

    [Test]
    procedure VisibleCondition_ShouldShowActive()
    begin
        // Proving the negated-boolean form works.
        Assert.IsTrue(Helper.ShouldShowActive(false), 'not false = true');
        Assert.IsFalse(Helper.ShouldShowActive(true), 'not true = false');
    end;

    [Test]
    procedure VisibleCondition_TableInCompilationUnit_Usable()
    var
        Item: Record "VCA Item";
    begin
        // Positive: the source table is usable — proves the whole compilation
        // unit (page + table + codeunit) is live end-to-end.
        Item.Init();
        Item."No." := 'I1';
        Item.Name := 'Widget';
        Item.Price := 9.99;
        Item.IsActive := true;
        Item.Insert();

        Item.Reset();
        Assert.IsTrue(Item.Get('I1'), 'Item I1 must be retrievable');
        Assert.AreEqual(9.99, Item.Price, 'Price must roundtrip');
        Assert.AreEqual(true, Item.IsActive, 'IsActive must roundtrip');
    end;

    [Test]
    procedure VisibleCondition_ShouldShowPrice_NotAlwaysTrue_NegativeTrap()
    begin
        // Negative: guard against ShouldShowPrice always returning true.
        Assert.AreNotEqual(
            Helper.ShouldShowPrice(5),
            Helper.ShouldShowPrice(-5),
            'ShouldShowPrice must differ for positive vs negative inputs');
    end;
}
