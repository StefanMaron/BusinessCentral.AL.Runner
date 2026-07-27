/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range).
/// </summary>
codeunit 60713 "ITS Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
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

    procedure ExpectedError(Expected: Text; Actual: Text)
    begin
        if StrPos(Actual, Expected) = 0 then
            Error('Assert.ExpectedError failed. Expected substring:<%1>. Actual:<%2>.', Expected, Actual);
    end;
}
