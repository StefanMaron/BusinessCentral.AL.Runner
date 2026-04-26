codeunit 59861 "UNE Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "UNE Src";

    [Test]
    procedure UnaryMinus_Positive_Negates()
    begin
        Assert.AreEqual(-5, Src.Negate(5), 'Unary minus on 5 must be -5');
    end;

    [Test]
    procedure UnaryMinus_Negative_ReturnsPositive()
    begin
        Assert.AreEqual(5, Src.Negate(-5), 'Unary minus on -5 must be 5');
    end;

    [Test]
    procedure UnaryMinus_Zero_ReturnsZero()
    begin
        Assert.AreEqual(0, Src.Negate(0), 'Unary minus on 0 must be 0');
    end;

    [Test]
    procedure UnaryMinus_Decimal()
    begin
        Assert.AreEqual(-3.14, Src.NegateDecimal(3.14), 'Unary minus on decimal 3.14 must be -3.14');
        Assert.AreEqual(2.5, Src.NegateDecimal(-2.5), 'Unary minus on -2.5 must be 2.5');
    end;

    [Test]
    procedure UnaryPlus_NoOp()
    begin
        // Unary plus must be a no-op (returns the value unchanged).
        Assert.AreEqual(7, Src.Identity(7), 'Unary plus on 7 must be 7');
        Assert.AreEqual(-3, Src.Identity(-3), 'Unary plus on -3 must be -3');
        Assert.AreEqual(0, Src.Identity(0), 'Unary plus on 0 must be 0');
    end;

    [Test]
    procedure LogicalNot_TrueBecomesFalse()
    begin
        Assert.IsFalse(Src.LogicalNot(true), 'not true must be false');
    end;

    [Test]
    procedure LogicalNot_FalseBecomesTrue()
    begin
        Assert.IsTrue(Src.LogicalNot(false), 'not false must be true');
    end;

    [Test]
    procedure LogicalNot_InIfBranch()
    begin
        // Proving: `not` works inside an if-condition branch.
        Assert.AreEqual('was-false', Src.NotInBranch(false),
            'if not false → was-false');
        Assert.AreEqual('was-true', Src.NotInBranch(true),
            'if not true → was-true');
    end;

    [Test]
    procedure UnaryMinus_InCompoundExpression()
    begin
        // `a + -b` must compute a - b.
        Assert.AreEqual(7, Src.NegateInExpression(10, 3), '10 + -3 must be 7');
        Assert.AreEqual(5, Src.NegateInExpression(2, -3), '2 + -(-3) must be 5');
    end;

    [Test]
    procedure UnaryMinus_Double()
    begin
        // -(-n) must be n.
        Assert.AreEqual(9, Src.NegateNegative(9), '-(-9) must be 9');
        Assert.AreEqual(-4, Src.NegateNegative(-4), '-(-(-4)) must be -4');
    end;

    [Test]
    procedure UnaryMinus_NotIdentity_NegativeTrap()
    begin
        // Negative: guard against a Negate that's actually identity.
        Assert.AreNotEqual(5, Src.Negate(5), 'Unary minus must flip sign, not identity');
    end;
}
