/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range).
/// </summary>
codeunit 60500 "Dialog NoOp Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure ExpectedError(Expected: Text; Actual: Text)
    begin
        if StrPos(Actual, Expected) = 0 then
            Error('Assert.ExpectedError failed. Expected substring:<%1>. Actual:<%2>.', Expected, Actual);
    end;
}
