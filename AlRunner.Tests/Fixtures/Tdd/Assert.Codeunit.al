/// <summary>Minimal assertion helper for this fixture app (own ID range).</summary>
codeunit 65000 "Tdd Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;
}
