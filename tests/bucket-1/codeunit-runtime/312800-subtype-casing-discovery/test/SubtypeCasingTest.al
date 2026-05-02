/// <summary>
/// Suite 312800 (bucket-1/codeunit-runtime): case-insensitive SubType property discovery.
///
/// Proves that the runner discovers test codeunits regardless of the casing
/// used for the property value:
///   SubType = Test;   (camelCase — Microsoft Learn canonical spelling, issue #1520)
///   subtype = test;   (all lowercase)
///
/// RED before fix: runner prints "No test codeunits found" and exits 0.
/// GREEN after fix: all four tests below run and pass.
///
/// IMPORTANT: Neither this file nor the src file must contain the exact literal
/// "Subtype" + " = Test" (capital-S, lower-case 't') in a comment, because the
/// pre-fix gate check is a case-sensitive substring search on the raw file text.
/// A comment containing that sequence would make the gate pass for the wrong
/// reason, hiding the RED phase.
///
/// The test codeunits assert non-default values (e.g. Add(3,4)=7, not 0) so a
/// no-op implementation cannot accidentally produce a green run.
/// </summary>

// -----------------------------------------------------------------
// Test codeunit using camelCase spelling  (SubType = Test)
// This is the exact spelling shown in Microsoft Learn docs.
// -----------------------------------------------------------------
codeunit 1312801 "SCD CamelCase Tests"
{
    SubType = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CamelCase_Add_ReturnsCorrectSum()
    var
        Helper: Codeunit "SCD Helper";
    begin
        // [GIVEN] Two positive integers
        // [WHEN]  Add is called via the helper
        // [THEN]  The sum is 7 (non-default, proves the test actually ran)
        Assert.AreEqual(7, Helper.Add(3, 4), 'CamelCase SubType: Add(3,4) must return 7');
    end;

    [Test]
    procedure CamelCase_Add_NegativeCase()
    var
        Helper: Codeunit "SCD Helper";
        Result: Integer;
    begin
        // [GIVEN] Two positive integers whose sum is 7
        Result := Helper.Add(3, 4);

        // [WHEN]  We assert a wrong expected value
        // [THEN]  Assert raises an error containing the message
        asserterror Assert.AreEqual(99, Result, 'Sum must not be 99');
        Assert.ExpectedError('Sum must not be 99');
    end;
}

// -----------------------------------------------------------------
// Test codeunit using all-lowercase spelling  (subtype = test)
// -----------------------------------------------------------------
codeunit 1312802 "SCD Lowercase Tests"
{
    subtype = test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure Lowercase_Add_ReturnsCorrectSum()
    var
        Helper: Codeunit "SCD Helper";
    begin
        // [GIVEN] Two positive integers
        // [WHEN]  Add is called via the helper
        // [THEN]  The sum is 10 (non-default, proves the test actually ran)
        Assert.AreEqual(10, Helper.Add(6, 4), 'Lowercase subtype: Add(6,4) must return 10');
    end;

    [Test]
    procedure Lowercase_Add_NegativeCase()
    var
        Helper: Codeunit "SCD Helper";
        Result: Integer;
    begin
        // [GIVEN] Two positive integers whose sum is 10
        Result := Helper.Add(6, 4);

        // [WHEN]  We assert a wrong expected value
        // [THEN]  Assert raises an error containing the message
        asserterror Assert.AreEqual(0, Result, 'Sum must not be 0');
        Assert.ExpectedError('Sum must not be 0');
    end;
}
