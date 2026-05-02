/// Regression tests for issue #1577.
///
/// Calling multi-arg overloads of the auto-stubbed Codeunit "No. Series"
/// (BaseApp ID 310) threw ArgumentException: 'NavDate cannot be converted to NavCode'
/// because ScoreMethodMatch picked the wrong 2-param overload (AreRelated vs GetNextNo)
/// and the retry catch missed ArgumentException (only caught TargetInvocationException).
///
/// All tests carry the _NoThrow suffix per the TDD rules: the entire claim is
/// "dispatch picks the right overload and the auto-stub returns the type default (empty Code)."
/// A constant-default mock would also pass these — the discriminating proof is in
/// MockCodeunitHandleScoreTests.cs (C# unit tests for ScoreMethodMatch tiers).
codeunit 1577002 "No Series GetNextNo OL Test"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetNextNo_OneArg_NoThrow()
    var
        NoSeries: Codeunit "No. Series";
        Result: Code[20];
    begin
        // [GIVEN] Auto-stubbed No. Series codeunit (1-arg overload)
        // [WHEN]  GetNextNo(Code[20]) is called
        // [THEN]  No exception is thrown; result is the type default (empty code)
        Result := NoSeries.GetNextNo('TEST');
        Assert.AreEqual('', Result, 'GetNextNo 1-arg must return empty Code (auto-stub default)');
    end;

    [Test]
    procedure GetNextNo_TwoArgs_NoThrow()
    var
        NoSeries: Codeunit "No. Series";
        Result: Code[20];
    begin
        // [GIVEN] Auto-stubbed No. Series codeunit (2-arg overload): Code[20], Date
        // [WHEN]  GetNextNo('TEST', WorkDate()) is called
        // [THEN]  No ArgumentException 'NavDate cannot be converted to NavCode' is thrown
        //         (this was the exact regression from issue #1577)
        Result := NoSeries.GetNextNo('TEST', WorkDate());
        Assert.AreEqual('', Result, 'GetNextNo 2-arg must return empty Code (auto-stub default)');
    end;

    [Test]
    procedure GetNextNo_ThreeArgs_NoThrow()
    var
        NoSeries: Codeunit "No. Series";
        Result: Code[20];
    begin
        // [GIVEN] Auto-stubbed No. Series codeunit (3-arg overload): Code[20], Date, Boolean
        // [WHEN]  GetNextNo('TEST', WorkDate(), false) is called
        // [THEN]  No exception is thrown; result is the type default (empty code)
        Result := NoSeries.GetNextNo('TEST', WorkDate(), false);
        Assert.AreEqual('', Result, 'GetNextNo 3-arg must return empty Code (auto-stub default)');
    end;
}
