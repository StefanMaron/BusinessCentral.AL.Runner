codeunit 60031 "MATH Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "MATH Src";

    // --- Abs ---

    [Test]
    procedure Abs_Negative_Decimal()
    begin
        Assert.AreEqual(42.5, Src.AbsDecimal(-42.5), 'Abs(-42.5) must be 42.5');
    end;

    [Test]
    procedure Abs_Positive_Decimal()
    begin
        Assert.AreEqual(7.25, Src.AbsDecimal(7.25), 'Abs(7.25) must be 7.25');
    end;

    [Test]
    procedure Abs_Zero_Decimal()
    begin
        Assert.AreEqual(0, Src.AbsDecimal(0), 'Abs(0) must be 0');
    end;

    [Test]
    procedure Abs_Negative_Integer()
    begin
        Assert.AreEqual(5, Src.AbsInteger(-5), 'Abs(-5) must be 5');
    end;

    [Test]
    procedure Abs_MaxInt()
    begin
        // Negative trap: if Abs were a no-op the call would return -5.
        Assert.AreNotEqual(-5, Src.AbsInteger(-5),
            'Abs must not leave a negative value negative');
    end;

    // --- Power ---

    [Test]
    procedure Power_Squared()
    begin
        Assert.AreEqual(16, Src.PowerIt(4, 2), '4^2 = 16');
    end;

    [Test]
    procedure Power_TwoToTheTen()
    begin
        Assert.AreEqual(1024, Src.PowerIt(2, 10), '2^10 = 1024');
    end;

    [Test]
    procedure Power_ZeroExponent()
    begin
        // Any non-zero base to the 0 power is 1.
        Assert.AreEqual(1, Src.PowerIt(7.5, 0), 'x^0 = 1');
    end;

    [Test]
    procedure Power_FractionalExponent()
    begin
        // 9^0.5 = 3 (square root).
        Assert.AreEqual(3, Src.PowerIt(9, 0.5), 'sqrt(9) via Power(9, 0.5) = 3');
    end;

    [Test]
    procedure Power_NegativeBase()
    begin
        // (-2)^3 = -8 — preserves sign for integer exponents.
        Assert.AreEqual(-8, Src.PowerIt(-2, 3), '(-2)^3 = -8');
    end;

    [Test]
    procedure Power_NotJustMultiplication()
    begin
        // Negative trap: make sure the impl doesn't just return base*exponent.
        Assert.AreNotEqual(20, Src.PowerIt(4, 5),
            'Power must not return base*exponent (would be 20)');
    end;

    // --- Round ---

    [Test]
    procedure Round_TwoArg_RoundsUp()
    begin
        // 3.456 rounded to 0.01 precision → 3.46 (half-even / standard rounding).
        Assert.AreEqual(3.46, Src.RoundTwoArg(3.456, 0.01), 'Round(3.456, 0.01) = 3.46');
    end;

    [Test]
    procedure Round_TwoArg_RoundsDown()
    begin
        Assert.AreEqual(3.45, Src.RoundTwoArg(3.454, 0.01), 'Round(3.454, 0.01) = 3.45');
    end;

    [Test]
    procedure Round_OneArg_ToInteger()
    begin
        Assert.AreEqual(4, Src.RoundOneArg(3.7), 'Round(3.7) = 4');
    end;

    [Test]
    procedure Round_ThreeArg_Up()
    begin
        // Direction '>' forces round-up.
        Assert.AreEqual(3.46, Src.RoundThreeArg(3.451, 0.01, '>'),
            'Round(3.451, 0.01, ''>'') forces rounding up to 3.46');
    end;

    [Test]
    procedure Round_ThreeArg_Down()
    begin
        // Direction '<' forces round-down.
        Assert.AreEqual(3.45, Src.RoundThreeArg(3.459, 0.01, '<'),
            'Round(3.459, 0.01, ''<'') forces rounding down to 3.45');
    end;

    [Test]
    procedure Round_ThreeArg_Nearest()
    begin
        // Direction '=' is standard nearest-rounding.
        Assert.AreEqual(3.46, Src.RoundThreeArg(3.456, 0.01, '='),
            'Round(3.456, 0.01, ''='') = 3.46');
    end;

    [Test]
    procedure Round_Precision10()
    begin
        // Coarser precision: 347 rounds to 350 with precision 10.
        Assert.AreEqual(350, Src.RoundTwoArg(347, 10), 'Round(347, 10) = 350');
    end;

    [Test]
    procedure Round_NegativeValue()
    begin
        Assert.AreEqual(-3.46, Src.RoundTwoArg(-3.456, 0.01),
            'Round(-3.456, 0.01) = -3.46');
    end;

    [Test]
    procedure Round_IsNotTruncation()
    begin
        // Negative trap: guard against Round being implemented as integer truncation.
        // 3.7 truncated = 3, but rounded = 4.
        Assert.AreNotEqual(3, Src.RoundOneArg(3.7),
            'Round(3.7) must not truncate to 3');
    end;
}
