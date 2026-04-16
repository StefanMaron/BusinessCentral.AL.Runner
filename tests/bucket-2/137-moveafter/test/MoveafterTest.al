codeunit 59512 "MAF Moveafter Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure PageExtMoveafter_CompilesAndHelperRuns()
    var
        Helper: Codeunit "MAF Product Helper";
    begin
        // Positive: the compilation unit containing pageextensions with `moveafter`
        // in layout and actions areas must compile, and codeunits defined alongside
        // them must be callable. This is what issue #432 says currently fails.
        Assert.AreEqual('moved', Helper.GetLabel(), 'Helper.GetLabel must return moved');
    end;

    [Test]
    procedure PageExtMoveafter_TriplePlusFour()
    var
        Helper: Codeunit "MAF Product Helper";
    begin
        // Proving: helper performs real work, not a no-op stub (issue #203 standard).
        Assert.AreEqual(10, Helper.TriplePlusFour(2), 'TriplePlusFour(2) must return 2*3+4=10');
        Assert.AreEqual(4, Helper.TriplePlusFour(0), 'TriplePlusFour(0) must return 0*3+4=4');
        Assert.AreEqual(-2, Helper.TriplePlusFour(-2), 'TriplePlusFour(-2) must return -6+4=-2');
    end;

    [Test]
    procedure PageExtMoveafter_TriplePlusFour_NotJustTriple()
    var
        Helper: Codeunit "MAF Product Helper";
    begin
        // Negative: guard against the no-op trap — TriplePlusFour must add the +4.
        Assert.AreNotEqual(6, Helper.TriplePlusFour(2), 'TriplePlusFour must not just return n*3');
    end;

    [Test]
    procedure PageExtMoveafter_Reverse()
    var
        Helper: Codeunit "MAF Product Helper";
    begin
        // Proving Reverse runs in same compilation unit as the pageextensions.
        Assert.AreEqual('olleh', Helper.Reverse('hello'), 'Reverse(hello) must be olleh');
        Assert.AreEqual('', Helper.Reverse(''), 'Reverse of empty must be empty');
        Assert.AreEqual('a', Helper.Reverse('a'), 'Reverse of single char is itself');
    end;

    [Test]
    procedure PageExtMoveafter_Reverse_NotIdentity()
    var
        Helper: Codeunit "MAF Product Helper";
    begin
        // Negative: Reverse must NOT simply return s (identity = no-op trap).
        Assert.AreNotEqual('hello', Helper.Reverse('hello'), 'Reverse must not return the input');
    end;

    [Test]
    procedure PageExtMoveafter_TableInCompilationUnit_Usable()
    var
        Product: Record "MAF Product";
    begin
        // Positive: the source table in the same compilation unit as the pageextensions
        // must be usable — insert and get work, proving the compilation unit is live.
        Product.Init();
        Product."No." := 'P1';
        Product.Name := 'Widget';
        Product.Category := 'Tools';
        Product.Insert();

        Product.Reset();
        Assert.IsTrue(Product.Get('P1'), 'Product P1 must be retrievable after Insert');
        Assert.AreEqual('Widget', Product.Name, 'Name must roundtrip through the table');
        Assert.AreEqual('Tools', Product.Category, 'Category must roundtrip through the table');
    end;
}
