/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range).
/// </summary>
codeunit 64265 "CFS Assert"
{
    procedure AreEqual(Expected: Decimal; Actual: Decimal; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure AreNotEqual(NotExpected: Decimal; Actual: Decimal; Msg: Text)
    begin
        if NotExpected = Actual then
            Error('Assert.AreNotEqual failed. Value:<%1>. %2', Actual, Msg);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure ExpectedError(Expected: Text; Actual: Text)
    begin
        if StrPos(Actual, Expected) = 0 then
            Error('Assert.ExpectedError failed. Expected substring:<%1>. Actual:<%2>.', Expected, Actual);
    end;
}
