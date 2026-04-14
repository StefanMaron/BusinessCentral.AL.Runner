codeunit 50910 "Cross Codeunit Tests"
{
    Subtype = Test;

    var
        StatsCalc: Codeunit "Stats Calculator";
        MathHelper: Codeunit "Math Helper";
        Assert: Codeunit Assert;

    [Test]
    procedure TestDirectCallToMathHelper()
    begin
        // [GIVEN/WHEN] Calling MathHelper directly
        // [THEN] Square(5) = 25
        Assert.AreEqual(25, MathHelper.Square(5), 'Square of 5 should be 25');
    end;

    [Test]
    procedure TestFactorial()
    begin
        Assert.AreEqual(1, MathHelper.Factorial(0), 'Factorial of 0 should be 1');
        Assert.AreEqual(1, MathHelper.Factorial(1), 'Factorial of 1 should be 1');
        Assert.AreEqual(120, MathHelper.Factorial(5), 'Factorial of 5 should be 120');
    end;

    [Test]
    procedure TestSumOfSquaresCrossCodeunit()
    var
        Result: Integer;
    begin
        // [GIVEN/WHEN] StatsCalculator calls MathHelper.Square internally
        Result := StatsCalc.SumOfSquares(3, 4);

        // [THEN] 3^2 + 4^2 = 9 + 16 = 25
        Assert.AreEqual(25, Result, 'Sum of squares of 3 and 4 should be 25');
    end;

    [Test]
    procedure TestPermutationsCrossCodeunit()
    var
        Result: Integer;
    begin
        // [GIVEN/WHEN] StatsCalculator calls MathHelper.Factorial internally
        Result := StatsCalc.Permutations(5, 2);

        // [THEN] P(5,2) = 5! / 3! = 120 / 6 = 20
        Assert.AreEqual(20, Result, 'Permutations P(5,2) should be 20');
    end;

    [Test]
    procedure TestMaxOfThreeCrossCodeunit()
    var
        Result: Integer;
    begin
        // [GIVEN/WHEN] StatsCalculator calls MathHelper.Max in nested fashion
        Result := StatsCalc.MaxOfThree(7, 15, 3);

        // [THEN] Max of 7, 15, 3 should be 15
        Assert.AreEqual(15, Result, 'Max of 7, 15, 3 should be 15');
    end;
}
