/// Helper codeunit for the assert-byref-coercion test suite.
/// Exercises the scenario from issue #1433: Assert.AreEqual called inside a
/// procedure that receives its operands as var Decimal params.  The BC
/// transpiler wraps these as ByRef<Decimal18>, and the runner must correctly
/// unwrap them before passing to the assertion.
codeunit 1312600 "Assert ByRef Helper"
{
    /// Call Assert.AreEqual with two values received as var Decimal parameters.
    /// Mirrors the pattern from issue #1433:
    ///   "CS1503: 'ByRef<Decimal18>' → 'ByRef<int>' (2×) [AL: Assert.AreEqual(Amount1, Amt, '...')]"
    procedure AssertDecimalEqual(var Amount1: Decimal; var Amt: Decimal; Msg: Text)
    var
        Assert: Codeunit Assert;
    begin
        Assert.AreEqual(Amount1, Amt, Msg);
    end;

    /// Same pattern but returning a Boolean instead of asserting, so the negative
    /// branch can be tested without asserterror.
    procedure DecimalsAreEqual(var Amount1: Decimal; var Amt: Decimal): Boolean
    begin
        exit(Amount1 = Amt);
    end;

    /// Var Integer variant: exercises ByRef<int> arg path through the helper.
    procedure AssertIntegerEqual(var Expected: Integer; var Actual: Integer; Msg: Text)
    var
        Assert: Codeunit Assert;
    begin
        Assert.AreEqual(Expected, Actual, Msg);
    end;
}
