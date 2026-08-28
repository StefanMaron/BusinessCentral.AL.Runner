/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range).
/// </summary>
codeunit 65200 "TPCD Assert"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1> Actual:<%2>. %3', Expected, Actual, Msg);
    end;
}
