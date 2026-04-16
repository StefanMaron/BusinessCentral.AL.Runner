codeunit 50982 "PXT PageExt Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExt_CompilesAndHelperRuns()
    var
        Helper: Codeunit "PXT Customer Helper";
    begin
        // Positive: the compilation unit containing a pageextension with layout
        // addafter, actions addafter, OnOpenPage / OnAfterGetCurrRecord triggers,
        // and local variables must compile — this is the comprehensive scenario
        // issue #370 asks for.
        Assert.AreEqual('[PXT] opened', Helper.BuildCaption('opened'),
            'Helper.BuildCaption must prefix context with [PXT] label');
    end;

    [Test]
    procedure PageExt_NextBalance_Positive()
    var
        Helper: Codeunit "PXT Customer Helper";
    begin
        // Proving: helper does real work, not a no-op (issue #203 standard).
        Assert.AreEqual(14.0, Helper.NextBalance(5, 8), 'NextBalance(5,8) must return 5+8+1=14');
        Assert.AreEqual(1.0, Helper.NextBalance(0, 0), 'NextBalance(0,0) must return 0+0+1=1');
        Assert.AreEqual(-1.5, Helper.NextBalance(-1.25, -1.25), 'NextBalance(-1.25,-1.25) must return -1.5');
    end;

    [Test]
    procedure PageExt_NextBalance_NotPlainSum()
    var
        Helper: Codeunit "PXT Customer Helper";
    begin
        // Negative: guard against the no-op trap — NextBalance must add the +1 bonus.
        Assert.AreNotEqual(13.0, Helper.NextBalance(5, 8), 'NextBalance must not just return current+delta');
    end;

    [Test]
    procedure PageExt_IsPositive()
    var
        Helper: Codeunit "PXT Customer Helper";
    begin
        // Proving IsPositive runs in same compilation unit as the pageextension.
        Assert.IsTrue(Helper.IsPositive(1), 'IsPositive(1) must be true');
        Assert.IsTrue(Helper.IsPositive(0.01), 'IsPositive(0.01) must be true');
        Assert.IsFalse(Helper.IsPositive(0), 'IsPositive(0) must be false');
        Assert.IsFalse(Helper.IsPositive(-1), 'IsPositive(-1) must be false');
    end;

    [Test]
    procedure PageExt_BuildCaption_PrefixPresent()
    var
        Helper: Codeunit "PXT Customer Helper";
    begin
        // Negative: BuildCaption must NOT return the raw context (would miss the [PXT] prefix).
        Assert.AreNotEqual('opened', Helper.BuildCaption('opened'),
            'BuildCaption must include the [PXT] prefix, not return the raw string');
    end;

    [Test]
    procedure PageExt_TableInCompilationUnit_Usable()
    var
        Customer: Record "PXT Customer";
    begin
        // Positive: the source table in the same compilation unit as the
        // pageextension must be usable — proves the whole compilation unit is live.
        Customer.Init();
        Customer."No." := 'C1';
        Customer.Name := 'Acme';
        Customer.Balance := 123.45;
        Customer.Insert();

        Customer.Reset();
        Assert.IsTrue(Customer.Get('C1'), 'Customer C1 must be retrievable after Insert');
        Assert.AreEqual('Acme', Customer.Name, 'Name must roundtrip');
        Assert.AreEqual(123.45, Customer.Balance, 'Balance must roundtrip');
    end;
}
