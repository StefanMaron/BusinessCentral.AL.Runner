codeunit 109001 "Rich Auto Stub Test"
{
    Subtype = Test;

    [Test]
    procedure CallDepMethod_ReturnsValue()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Integer;
    begin
        // [GIVEN] A codeunit from the test toolkit range
        // [WHEN] Call a method on it
        Result := Helper.ComputeValue(5);
        // [THEN] Returns the computed value (10, since source is compiled here)
        Assert.AreEqual(10, Result, 'ComputeValue should return input * 2');
    end;

    [Test]
    procedure CallDepMethod_ReturnsText()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Text;
    begin
        Result := Helper.FormatLabel('Item-', 42);
        Assert.AreEqual('Item-42', Result, 'FormatLabel should concatenate prefix and value');
    end;

    [Test]
    procedure CallDepVoidMethod_DoesNotCrash()
    var
        Helper: Codeunit "Rich Stub Helper";
    begin
        // [SCENARIO] Calling a void method on an auto-stubbed codeunit must not crash.
        // Auto-stubs generate empty method bodies for procedures with no return value.
        Helper.DoSetup();
        // If we reach here without error, the void dispatch succeeded.
    end;

    [Test]
    procedure CallDepMethod_ReturnsBooleanDefault()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Boolean;
    begin
        // [SCENARIO] Auto-stubbed Boolean methods return false (the default).
        Result := Helper.IsReady();
        Assert.IsFalse(Result, 'Auto-stub Boolean method should return false (default)');
    end;

    [Test]
    procedure CallDepMethod_ReturnsDecimalDefault()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Decimal;
    begin
        // [SCENARIO] Auto-stubbed Decimal methods return 0 (the default).
        Result := Helper.GetAmount();
        Assert.AreEqual(0, Result, 'Auto-stub Decimal method should return 0 (default)');
    end;

    [Test]
    procedure CallDepMethod_ReturnsCodeDefault()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Code[20];
    begin
        // [SCENARIO] Auto-stubbed Code methods return '' (the default).
        Result := Helper.GetCode();
        Assert.AreEqual('', Result, 'Auto-stub Code method should return empty (default)');
    end;

    [Test]
    procedure CallDepMethod_MultiParam_ReturnsValue()
    var
        Helper: Codeunit "Rich Stub Helper";
        Assert: Codeunit "Library Assert";
        Result: Text;
    begin
        // [SCENARIO] Methods with multiple parameters dispatch correctly.
        // This verifies the auto-stub dispatch handles multi-arg methods,
        // not just single-arg or zero-arg ones.
        Result := Helper.Combine(3, 7, 'Sum=');
        Assert.AreEqual('Sum=10', Result, 'Combine should concatenate prefix with sum of args');
    end;
}
