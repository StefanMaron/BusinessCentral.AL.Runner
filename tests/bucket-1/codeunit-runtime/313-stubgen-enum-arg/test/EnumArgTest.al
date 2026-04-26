/// Regression tests for issue #1419 — auto-stubbed codeunit methods must
/// preserve Enum/Option parameter types.
///
/// These tests compile both caller and callee from source, exercising the
/// fundamental runtime pattern (Enum literal → codeunit method param).  The
/// companion C# test (AutoStubEnumParamTests.cs) exercises the actual stub
/// generator path when alc.exe is available.
codeunit 1313003 "Enum Arg Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Enum Arg Helper";

    // ── Positive cases ─────────────────────────────────────────────────────────

    [Test]
    procedure EnumLiteral_Active_ReturnsCorrectOrdinal()
    begin
        // [GIVEN] The "Active" enum literal (ordinal 1)
        // [WHEN]  Passed to a method with Enum parameter
        // [THEN]  The method returns ordinal 1 — proves the value was not discarded
        Assert.AreEqual(1, Helper.GetStatusOrdinal("Enum Arg Status"::Active),
            'Active enum literal (ordinal 1) must be conveyed through Enum parameter');
    end;

    [Test]
    procedure EnumLiteral_Inactive_ReturnsCorrectOrdinal()
    begin
        // [GIVEN] The "Inactive" enum literal (ordinal 2)
        // [WHEN]  Passed to a method with Enum parameter
        // [THEN]  The method returns ordinal 2 (non-default — stub can't fake this)
        Assert.AreEqual(2, Helper.GetStatusOrdinal("Enum Arg Status"::Inactive),
            'Inactive enum literal (ordinal 2) must be conveyed through Enum parameter');
    end;

    [Test]
    procedure EnumVar_Passed_ReturnsCorrectOrdinal()
    var
        Status: Enum "Enum Arg Status";
    begin
        // [GIVEN] An Enum variable set to a non-default value
        Status := "Enum Arg Status"::Pending;
        // [WHEN]  Passed to a method with Enum parameter
        // [THEN]  Returns ordinal 3 — the variable value is preserved
        Assert.AreEqual(3, Helper.GetStatusOrdinal(Status),
            'Enum variable (Pending = ordinal 3) must be conveyed through Enum parameter');
    end;

    [Test]
    procedure MultiEnumParam_MaxStatus_ReturnsHigherValue()
    var
        Result: Enum "Enum Arg Status";
    begin
        // [GIVEN] Two different Enum literals
        // [WHEN]  Passed to a multi-Enum-param method
        // [THEN]  The method returns the one with higher ordinal
        Result := Helper.MaxStatus("Enum Arg Status"::Active, "Enum Arg Status"::Inactive);
        Assert.AreEqual("Enum Arg Status"::Inactive, Result,
            'MaxStatus(Active, Inactive) must return Inactive (ordinal 2 > ordinal 1)');
    end;

    [Test]
    procedure OptionParam_IntegerLiteral_RoundTrips()
    begin
        // [GIVEN] An integer literal representing a legacy Option value
        // [WHEN]  Passed to a method with Option parameter
        // [THEN]  The method returns the same integer (no cast error)
        Assert.AreEqual(2, Helper.GetOptionValue(2),
            'Option parameter must accept integer literal 2 and return it unchanged');
    end;

    // ── Negative cases ─────────────────────────────────────────────────────────

    [Test]
    procedure DefaultEnumLiteral_ReturnsZeroOrdinal()
    begin
        // [GIVEN] The default (empty) enum value (ordinal 0)
        // [WHEN]  Passed as an enum literal
        // [THEN]  Returns 0 — confirms default/zero path also works
        Assert.AreEqual(0, Helper.GetStatusOrdinal("Enum Arg Status"::" "),
            'Default enum literal (ordinal 0) must return 0 through Enum parameter');
    end;

    [Test]
    procedure OptionParam_ZeroLiteral_ReturnsZero()
    begin
        // [GIVEN] Option value 0 (the default)
        // [WHEN]  Passed to Option parameter
        // [THEN]  Returns 0 — zero path is distinct from a crash
        Assert.AreEqual(0, Helper.GetOptionValue(0),
            'Option parameter with value 0 must return 0');
    end;
}
