/// <summary>
/// #5228: a test calls an existing procedure with one more argument (AL0126). The generator adds an
/// overload next to the existing one; the existing one-argument procedure keeps working.
/// </summary>
codeunit 65204 "Tdd Shape Overload Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Shape Assert";

    [Test]
    procedure ExistingWithExtraParam_RunsAgainstGeneratedOverload()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.Existing(5, 7);
        Assert.AreEqual(0, Result, 'an empty generated overload returns 0');
    end;

    [Test]
    procedure ExistingWithExtraParam_FailsOnItsOwnAssertion()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.Existing(5, 7);
        Assert.AreEqual(12, Result, 'Existing(5, 7) should be 12');
    end;

    [Test]
    procedure ExistingWithItsOwnParameters_IsNotAnnotated()
    var
        Target: Codeunit "Tdd Shape Target Cu";
    begin
        Assert.AreEqual(5, Target.Existing(5), 'the existing procedure still returns its argument');
    end;

    [Test]
    procedure SameArityDifferentTypes_GetTwoOverloads()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.Existing(1, 2);
        Assert.AreEqual(0, Result, 'the (Integer; Integer) overload returns 0');
        Result := Target.Existing(1.5, 2);
        Assert.AreEqual(0, Result, 'the (Decimal; Integer) overload returns 0');
    end;
}
