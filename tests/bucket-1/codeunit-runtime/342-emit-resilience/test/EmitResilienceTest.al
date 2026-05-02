// Tests for emit-resilience patterns (issue #1554).
// Verifies that codeunit try-function error propagation compiles and executes
// correctly; these are the patterns that were at risk when BC's emit phase
// threw exceptions for unresolved NavTypeKind.None type references.
codeunit 50086 "Emit Resilience Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Emit Resilience Helper";

    [Test]
    procedure DivideInts_Positive_ReturnsQuotient()
    begin
        // Positive: normal integer division
        Assert.AreEqual(5, Helper.DivideInts(10, 2), 'DivideInts(10,2) must return 5');
    end;

    [Test]
    procedure DivideInts_Remainder_Truncates()
    begin
        // Positive: div truncates (not rounds)
        Assert.AreEqual(3, Helper.DivideInts(7, 2), 'DivideInts(7,2) must return 3 (integer truncation)');
    end;

    [Test]
    procedure DivideInts_ByZero_RaisesError()
    begin
        // Negative: division by zero must raise an error with the expected message
        asserterror Helper.DivideInts(1, 0);
        Assert.ExpectedError('Division by zero');
    end;

    [Test]
    procedure SafeDivide_ValidDenominator_ReturnsTrueAndResult()
    var
        Result: Integer;
        Success: Boolean;
    begin
        // Positive: try-function succeeds → returns true, result is set
        Success := Helper.SafeDivide(20, 4, Result);
        Assert.IsTrue(Success, 'SafeDivide with valid denominator must return true');
        Assert.AreEqual(5, Result, 'SafeDivide(20,4) result must be 5');
    end;

    [Test]
    procedure SafeDivide_ZeroDenominator_ReturnsFalse()
    var
        Result: Integer;
        Success: Boolean;
    begin
        // Negative: try-function catches error → returns false, result unchanged (0)
        Result := 99;
        Success := Helper.SafeDivide(10, 0, Result);
        Assert.IsFalse(Success, 'SafeDivide with zero denominator must return false');
    end;

    [Test]
    procedure ConcatWithSeparator_BothNonEmpty_ReturnsCombined()
    begin
        // Positive: both parts present → joined with separator
        Assert.AreEqual('foo-bar', Helper.ConcatWithSeparator('foo', '-', 'bar'), 'concat("foo","-","bar") must be "foo-bar"');
    end;

    [Test]
    procedure ConcatWithSeparator_EmptyA_ReturnsB()
    begin
        // Positive: empty A → returns B directly (no leading separator)
        Assert.AreEqual('bar', Helper.ConcatWithSeparator('', '-', 'bar'), 'concat("","-","bar") must be "bar"');
    end;

    [Test]
    procedure ConcatWithSeparator_EmptyB_ReturnsA()
    begin
        // Positive: empty B → returns A directly (no trailing separator)
        Assert.AreEqual('foo', Helper.ConcatWithSeparator('foo', '-', ''), 'concat("foo","-","") must be "foo"');
    end;
}
