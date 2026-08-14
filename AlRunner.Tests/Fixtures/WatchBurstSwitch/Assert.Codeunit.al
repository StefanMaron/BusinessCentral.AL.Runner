/// <summary>
/// Minimal assertion helper for this fixture app (own ID range so it
/// stands alone from the corpus Assert). Identical in both "versions" —
/// WatchBurstSwitchTests never edits this file.
/// </summary>
codeunit 60250 "Burst Assert RXT"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;
}
