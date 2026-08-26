/// <summary>
/// Minimal assertion helper for this fixture app (own ID range so it
/// stands alone from the corpus Assert).
/// </summary>
codeunit 60900 "Dap Step Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;
}
