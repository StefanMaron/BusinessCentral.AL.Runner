/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range).
/// </summary>
codeunit 62400 "CSFP Assert"
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
}
