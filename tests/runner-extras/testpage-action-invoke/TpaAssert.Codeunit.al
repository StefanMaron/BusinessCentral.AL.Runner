/// <summary>Minimal assertion helper for this runner-extras app (own ID range).</summary>
codeunit 61944 "TPA Assert"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

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

    /// <summary>Asserts the last error's text contains <paramref name="Expected"/>.</summary>
    procedure ExpectedError(Expected: Text)
    begin
        if GetLastErrorText() = '' then
            Error('Assert.ExpectedError failed. Expected an error containing <%1>, but none was raised.', Expected);
        if StrPos(GetLastErrorText(), Expected) = 0 then
            Error('Assert.ExpectedError failed. Expected an error containing <%1>. Actual:<%2>.',
                Expected, GetLastErrorText());
    end;
}
