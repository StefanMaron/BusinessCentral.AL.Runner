/// <summary>
/// Suite 01 (bucket-1/codeunit-runtime): Executor parameter-count fix.
///
/// When a test codeunit contains a helper procedure whose name starts with "Test"
/// but is NOT a [Test] method (it has parameters), the BC compiler generates a
/// _Scope_ class whose constructor has MORE than one parameter (parent + each AL
/// parameter).  Before the fix, RunTests() called
///   ctors[0].Invoke(new[] { parent })
/// which throws "Parameter count mismatch" for such a scope.
///
/// After the fix, the executor supplies default values for extra ctor params so
/// the scope is constructed correctly and execution continues.
/// </summary>
codeunit 310001 "ECP Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "ECP Helper";

    // -----------------------------------------------------------------
    // Positive: basic arithmetic round-trips through the helper codeunit
    // -----------------------------------------------------------------

    [Test]
    procedure Multiply_TwoPositives_ReturnsProduct()
    begin
        // [GIVEN] Two positive integers
        // [WHEN] Multiply is called
        // [THEN] Returns the product
        Assert.AreEqual(12, Helper.Multiply(3, 4), 'Multiply(3,4) must return 12');
    end;

    [Test]
    procedure Multiply_ByZero_ReturnsZero()
    begin
        // [GIVEN] One factor is zero
        // [WHEN] Multiply is called
        // [THEN] Returns 0 (not a default that would pass a no-op stub)
        Assert.AreEqual(0, Helper.Multiply(5, 0), 'Multiply(5,0) must return 0');
        Assert.AreEqual(0, Helper.Multiply(0, 7), 'Multiply(0,7) must return 0');
    end;

    [Test]
    procedure Add_TwoIntegers_ReturnsSum()
    begin
        Assert.AreEqual(7, Helper.Add(3, 4), 'Add(3,4) must return 7');
    end;

    // -----------------------------------------------------------------
    // This is the key trigger for issue-1200:
    // A helper named TestXxx WITH parameters generates a _Scope_ class
    // whose constructor has (parent, param1, param2, ...).
    // The [Test] method below calls it; the executor must not crash when
    // it encounters the TestVerifyProduct_Scope_xxx type.
    // -----------------------------------------------------------------

    [Test]
    procedure TestHelperWithParams_DoesNotCrashExecutor()
    var
        Result: Integer;
    begin
        // [GIVEN] A helper procedure whose name starts with "Test" and takes params
        // [WHEN] The executor runs all tests in this codeunit
        // [THEN] No "Parameter count mismatch" error is thrown; the test runs normally
        Result := TestComputeProduct(6, 7);
        Assert.AreEqual(42, Result, 'TestComputeProduct(6,7) must return 42');
    end;

    [Test]
    procedure TestHelperWithParams_Negative_WrongResult()
    var
        Result: Integer;
    begin
        // [GIVEN] A known product
        // [WHEN] The result is compared to a wrong expected value using asserterror
        Result := TestComputeProduct(3, 3);
        asserterror Assert.AreEqual(10, Result, 'Must fail: 3*3 is not 10');
        Assert.ExpectedError('3*3 is not 10');
    end;

    /// <summary>
    /// Helper procedure whose name starts with "Test" AND has parameters.
    /// BC emits a _Scope_ class with constructor (Codeunit310001 parent, Decimal18 a, Decimal18 b).
    /// Before fix: RunTests() crashes with "Parameter count mismatch" when it
    /// tries to instantiate this scope.
    /// After fix: extra params receive default values; the scope is created correctly.
    /// </summary>
    procedure TestComputeProduct(A: Integer; B: Integer): Integer
    begin
        exit(A * B);
    end;
}
