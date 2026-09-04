/// <summary>
/// Minimal assertion helper for this fixture app (own ID range so it stands
/// alone from the corpus Assert).
/// </summary>
codeunit 60840 "FFR Assert"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure ExpectedError(Expected: Text; Actual: Text)
    begin
        if StrPos(Actual, Expected) = 0 then
            Error('Assert.ExpectedError failed. Expected to find:<%1>. Actual:<%2>.', Expected, Actual);
    end;
}
