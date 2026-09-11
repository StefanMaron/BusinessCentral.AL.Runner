/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range, no dependency on
/// the al-language submodule's fixtures).
/// </summary>
codeunit 64570 "PMN Assert"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1> Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1> Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure IsTrue(Actual: Boolean; Msg: Text)
    begin
        if not Actual then
            Error('Assert.IsTrue failed. Expected:<true> Actual:<false>. %1', Msg);
    end;

    procedure IsFalse(Actual: Boolean; Msg: Text)
    begin
        if Actual then
            Error('Assert.IsFalse failed. Expected:<false> Actual:<true>. %1', Msg);
    end;

    /// <summary>The last error text must CONTAIN Expected - a substring match, so a test
    /// can pin the meaningful part of a message that also carries a variable id.</summary>
    procedure ExpectedError(Expected: Text)
    begin
        if StrPos(GetLastErrorText(), Expected) = 0 then
            Error('Assert.ExpectedError failed. Expected:<%1> Actual:<%2>.', Expected, GetLastErrorText());
    end;
}
