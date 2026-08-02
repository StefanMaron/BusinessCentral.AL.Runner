/// <summary>
/// Minimal assertion helper for this fixture app (own ID range so it
/// stands alone from the corpus Assert).
/// </summary>
codeunit 60150 "xRec Assert RXT"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;
}
