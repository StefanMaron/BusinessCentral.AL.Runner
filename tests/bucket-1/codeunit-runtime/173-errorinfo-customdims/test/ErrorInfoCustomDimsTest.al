codeunit 59941 "EICD Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EICD Src";

    [Test]
    procedure CustomDimensions_SetAndGet_CountPreserved()
    begin
        Assert.AreEqual(2, Src.SetAndGetCount(),
            'CustomDimensions must preserve 2 added entries');
    end;

    [Test]
    procedure CustomDimensions_Fresh_IsEmpty()
    begin
        Assert.AreEqual(0, Src.FreshCount(),
            'Fresh ErrorInfo CustomDimensions must have Count 0');
    end;

    [Test]
    procedure CustomDimensions_ValueRetrievable()
    begin
        // Proving the value stored under a key is retrievable.
        Assert.AreEqual('world', Src.SetAndGetValue('hello', 'world'),
            'CustomDimensions must allow retrieving values by key');
    end;

    [Test]
    procedure CustomDimensions_LastWriteWins()
    begin
        // Setting CustomDimensions with a new dictionary must replace the prior one,
        // not merge — the new count (1) wins over the old (3).
        Assert.AreEqual(1, Src.LastWriteWins_NewCount(),
            'Subsequent CustomDimensions setter must replace, not merge');
    end;

    [Test]
    procedure CustomDimensions_DifferentValues_DifferentResults_NegativeTrap()
    begin
        Assert.AreNotEqual(
            Src.SetAndGetValue('k', 'alpha'),
            Src.SetAndGetValue('k', 'beta'),
            'Different stored values must produce different retrievals');
    end;
}
