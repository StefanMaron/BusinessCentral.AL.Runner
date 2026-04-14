codeunit 53900 "Test Stubs"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure StubReplacesRealImplementation()
    var
        Rec: Record "Stub Table";
        Logic: Codeunit "Business Logic";
        Result: Decimal;
    begin
        // Positive: stub doubles (2x) instead of real impl which triples (3x)
        Rec.Init();
        Rec."No." := 'S1';
        Rec."Value" := 10;
        Result := Logic.RunWithProcessor(Rec);
        // If stub is used: 10 * 2 = 20
        // If real impl: 10 * 3 = 30
        Assert.AreEqual(20, Result, 'Stub should double the value (2x), not triple (3x)');
    end;

    [Test]
    procedure StubNotRealImpl()
    var
        Rec: Record "Stub Table";
        Logic: Codeunit "Business Logic";
        Result: Decimal;
    begin
        // Negative: verify we're not using the real implementation
        Rec.Init();
        Rec."No." := 'S2';
        Rec."Value" := 5;
        Result := Logic.RunWithProcessor(Rec);
        Assert.AreNotEqual(15, Result, 'Should NOT be 5 * 3 = 15 (real impl)');
    end;
}
