codeunit 306001 "Version Create String Test"
{
    Subtype = Test;

    var
        Assert: Codeunit "Library Assert";
        Src: Codeunit "Version Create String Src";

    /// Positive: variable vs variable comparison works (baseline, existing coverage)
    [Test]
    procedure VariableVsVariable_LessOrEqual()
    begin
        Assert.IsTrue(Src.IsVersionAtOrBelow('1.0.0.0', '2.0.0.0'),
            '1.0.0.0 <= 2.0.0.0 must be true');
        Assert.IsFalse(Src.IsVersionAtOrBelow('3.0.0.0', '2.0.0.0'),
            '3.0.0.0 <= 2.0.0.0 must be false');
    end;

    /// Positive: inline Version.Create(var) <= Version.Create('literal') — core issue #1322
    [Test]
    procedure InlineLiteralOnRight_AtOrBelow()
    begin
        // Proves the string overload works: 0.9.0.0 <= 1.0.0.0
        Assert.IsTrue(Src.IsAtOrBelow1000('0.9.0.0'),
            '0.9.0.0 <= 1.0.0.0 must be true');
        // Negative direction: 1.1.0.0 > 1.0.0.0 so not <=
        Assert.IsFalse(Src.IsAtOrBelow1000('1.1.0.0'),
            '1.1.0.0 <= 1.0.0.0 must be false');
    end;

    /// Positive: inline Version.Create('literal') < Version.Create(var) — literal on left side
    [Test]
    procedure InlineLiteralOnLeft_Above()
    begin
        // 1.0.0.0 < 2.0.0.0 is true
        Assert.IsTrue(Src.IsAbove1000('2.0.0.0'),
            '1.0.0.0 < 2.0.0.0 must be true');
        // 1.0.0.0 < 1.0.0.0 is false
        Assert.IsFalse(Src.IsAbove1000('1.0.0.0'),
            '1.0.0.0 < 1.0.0.0 must be false');
    end;

    /// Positive: MajorFromLiteral returns non-default value, proving string is correctly parsed
    [Test]
    procedure MajorFromLiteral_ReturnsCorrectValue()
    begin
        // Must return 25, not 0 — proves the string overload actually parses '25.3.10000.5'
        Assert.AreEqual(25, Src.MajorFromLiteral(), 'Major from string literal must be 25');
    end;
}
