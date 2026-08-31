/// <summary>
/// Local assertion helper, following the tests/runner-extras/ convention (see
/// microsoft-test-library/MTLAssert.Codeunit.al): a runner-extras bundle carries its own
/// tiny Assert rather than depending on the Microsoft test toolkit, so the bundle compiles
/// against the platform apps alone.
/// </summary>
codeunit 64401 "TDF Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Assert.IsFalse failed. %1', Msg);
    end;

    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    /// <summary>
    /// Assert the last error contains <paramref name="ExpectedFragment"/>. A bare
    /// `asserterror` passes for ANY error, including one thrown for a reason the test never
    /// intended, so a negative case that means something has to name the message it expects.
    /// </summary>
    procedure ExpectedError(ExpectedFragment: Text)
    var
        Actual: Text;
    begin
        Actual := GetLastErrorText();
        if Actual = '' then
            Error('Assert.ExpectedError failed. No error was raised. Expected it to contain:<%1>.', ExpectedFragment);
        if StrPos(Actual, ExpectedFragment) = 0 then
            Error('Assert.ExpectedError failed. Expected the error to contain:<%1>. Actual:<%2>.',
                ExpectedFragment, Actual);
    end;
}
