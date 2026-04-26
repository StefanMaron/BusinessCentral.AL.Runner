/// Regression test for issue #1433 — CS1503: ByRef conversion error in Assert.AreEqual.
///
/// When Assert.AreEqual is called inside a procedure that receives its operands
/// as "var Decimal" params, the BC transpiler wraps those as ByRef<Decimal18>.
/// The runner must compile and execute that pattern without CS1503.
///
/// Covers both directions:
///   Positive — equal values → assertion passes.
///   Negative — unequal values → assertion fires the expected error.
codeunit 1312601 "Assert ByRef Coercion Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Assert ByRef Helper";

    // ── Decimal var-param path ─────────────────────────────────────────────────

    [Test]
    procedure AssertAreEqual_VarDecimal_EqualValues_Passes()
    var
        Amount1: Decimal;
        Amt: Decimal;
    begin
        // [GIVEN] Two equal Decimal locals that will be passed as var params
        Amount1 := 123.45;
        Amt := 123.45;
        // [WHEN] AreEqual is invoked through a procedure with var Decimal params
        // [THEN] No error is raised — the ByRef<Decimal18> coercion must succeed
        Helper.AssertDecimalEqual(Amount1, Amt, 'Decimals should be equal');
    end;

    [Test]
    procedure AssertAreEqual_VarDecimal_UnequalValues_Fails()
    var
        Amount1: Decimal;
        Amt: Decimal;
    begin
        // [GIVEN] Two unequal Decimal locals passed as var params
        Amount1 := 100;
        Amt := 200;
        // [WHEN/THEN] AreEqual fires the mismatch error
        asserterror Helper.AssertDecimalEqual(Amount1, Amt, 'Expected 100 = 200');
        Assert.ExpectedError('Expected 100 = 200');
    end;

    [Test]
    procedure DecimalsAreEqual_VarDecimal_SameValue_ReturnsTrue()
    var
        Amount1: Decimal;
        Amt: Decimal;
        Result: Boolean;
    begin
        // [GIVEN] Equal Decimal vars
        Amount1 := 42.5;
        Amt := 42.5;
        // [WHEN] Compared through helper with var Decimal params
        Result := Helper.DecimalsAreEqual(Amount1, Amt);
        // [THEN] Returns true (non-default value proves mock is working)
        Assert.IsTrue(Result, 'Equal decimals must return true');
    end;

    [Test]
    procedure DecimalsAreEqual_VarDecimal_DifferentValues_ReturnsFalse()
    var
        Amount1: Decimal;
        Amt: Decimal;
        Result: Boolean;
    begin
        // [GIVEN] Different Decimal vars
        Amount1 := 1;
        Amt := 2;
        // [WHEN] Compared through helper
        Result := Helper.DecimalsAreEqual(Amount1, Amt);
        // [THEN] Returns false
        Assert.IsFalse(Result, 'Unequal decimals must return false');
    end;

    // ── Integer var-param path ─────────────────────────────────────────────────

    [Test]
    procedure AssertAreEqual_VarInteger_EqualValues_Passes()
    var
        Expected: Integer;
        Actual: Integer;
    begin
        // [GIVEN] Equal Integer locals passed as var params
        Expected := 7;
        Actual := 7;
        // [WHEN/THEN] No error (ByRef<int> must be coerced correctly)
        Helper.AssertIntegerEqual(Expected, Actual, 'Integers should match');
    end;

    [Test]
    procedure AssertAreEqual_VarInteger_UnequalValues_Fails()
    var
        Expected: Integer;
        Actual: Integer;
    begin
        // [GIVEN] Unequal Integer locals passed as var params
        Expected := 3;
        Actual := 9;
        // [WHEN/THEN] AreEqual fires
        asserterror Helper.AssertIntegerEqual(Expected, Actual, 'Expected 3 = 9');
        Assert.ExpectedError('Expected 3 = 9');
    end;
}
