codeunit 60001 "TE Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: ternary expression evaluates the correct branch.
    // ------------------------------------------------------------------

    [Test]
    procedure TernaryExpr_TrueBranch()
    var
        H: Codeunit "TE Helper";
    begin
        // [GIVEN] x = 5 (> 3)
        // [WHEN]  Classify(5) using ternary if x > 3 then 'big' else 'small'
        // [THEN]  Returns 'big'
        Assert.AreEqual('big', H.Classify(5), 'Classify(5) must return big');
    end;

    [Test]
    procedure TernaryExpr_FalseBranch()
    var
        H: Codeunit "TE Helper";
    begin
        // [GIVEN] x = 2 (not > 3)
        // [WHEN]  Classify(2)
        // [THEN]  Returns 'small'
        Assert.AreEqual('small', H.Classify(2), 'Classify(2) must return small');
    end;

    [Test]
    procedure TernaryExpr_BoundaryFalse()
    var
        H: Codeunit "TE Helper";
    begin
        // [GIVEN] x = 3 (not > 3, equal)
        // [THEN]  Returns 'small' (boundary is exclusive)
        Assert.AreEqual('small', H.Classify(3), 'Classify(3) must return small (boundary)');
    end;

    [Test]
    procedure TernaryExpr_Assign_TrueBranch()
    var
        H: Codeunit "TE Helper";
    begin
        // [THEN]  Ternary used in assignment must also evaluate correctly
        Assert.AreEqual('big', H.ClassifyAssign(10), 'ClassifyAssign(10) must return big');
        Assert.AreEqual('small', H.ClassifyAssign(1), 'ClassifyAssign(1) must return small');
    end;

    [Test]
    procedure TernaryExpr_Nested_Large()
    var
        H: Codeunit "TE Helper";
    begin
        // [THEN]  x = 15 → large
        Assert.AreEqual('large', H.ClassifyThreeWay(15), 'ClassifyThreeWay(15) must return large');
    end;

    [Test]
    procedure TernaryExpr_Nested_Medium()
    var
        H: Codeunit "TE Helper";
    begin
        // [THEN]  x = 7 → medium (7 > 3 but not > 10)
        Assert.AreEqual('medium', H.ClassifyThreeWay(7), 'ClassifyThreeWay(7) must return medium');
    end;

    [Test]
    procedure TernaryExpr_Nested_Small()
    var
        H: Codeunit "TE Helper";
    begin
        // [THEN]  x = 1 → small
        Assert.AreEqual('small', H.ClassifyThreeWay(1), 'ClassifyThreeWay(1) must return small');
    end;

    [Test]
    procedure TernaryExpr_EvenOdd()
    var
        H: Codeunit "TE Helper";
    begin
        Assert.AreEqual('even', H.EvenOrOdd(4), 'EvenOrOdd(4) must return even');
        Assert.AreEqual('odd', H.EvenOrOdd(7), 'EvenOrOdd(7) must return odd');
        Assert.AreEqual('even', H.EvenOrOdd(0), 'EvenOrOdd(0) must return even');
    end;

    [Test]
    procedure TernaryExpr_MaxOf()
    var
        H: Codeunit "TE Helper";
    begin
        // [THEN]  MaxOf returns the larger of the two integers
        Assert.AreEqual(9, H.MaxOf(9, 3), 'MaxOf(9,3) must return 9');
        Assert.AreEqual(9, H.MaxOf(3, 9), 'MaxOf(3,9) must return 9');
        Assert.AreEqual(5, H.MaxOf(5, 5), 'MaxOf(5,5) must return 5');
    end;

    // ------------------------------------------------------------------
    // Negative: proves the result is determined by the condition,
    // not always one branch.
    // ------------------------------------------------------------------

    [Test]
    procedure TernaryExpr_TrueAndFalse_AreDifferent()
    var
        H: Codeunit "TE Helper";
    begin
        // [THEN]  The two branches must return different results
        Assert.AreNotEqual(H.Classify(5), H.Classify(1), 'True and false branches must differ');
    end;

    [Test]
    procedure TernaryExpr_MaxOf_OrderMatters()
    var
        H: Codeunit "TE Helper";
    begin
        // [THEN]  MaxOf(9,3) != 3  — proves it doesn't always return the second arg
        Assert.AreNotEqual(3, H.MaxOf(9, 3), 'MaxOf must not always return the second argument');
    end;
}
