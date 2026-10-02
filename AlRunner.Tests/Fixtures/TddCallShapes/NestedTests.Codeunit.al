/// <summary>
/// #5146: the missing procedure is an argument of Assert.AreEqual, whose parameters are Variant.
/// The type comes from the other Variant argument (the expected value), the way an assignment's
/// target types the return value.
/// </summary>
codeunit 65202 "Tdd Shape Nested Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Shape Assert";

    [Test]
    procedure NestedIntegerExpected_FailsOnItsOwnAssertion()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Assert.AreEqual(25, Target.CalcPoints(250), 'CalcPoints(250) should be 25');
    end;

    [Test]
    procedure NestedBooleanExpected_FailsOnItsOwnAssertion()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Assert.AreEqual(true, Target.IsGold(250), 'IsGold(250) should be true');
    end;

    [Test]
    procedure NestedDecimalVariableExpected_PassesAgainstTheDefault()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Expected: Decimal;
    begin
        Assert.AreEqual(Expected, Target.CalcRate(250), 'an empty generated stub returns 0');
    end;
}
