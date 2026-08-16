/// <summary>Minimal assertion helper for this fixture app (own ID range).</summary>
codeunit 50920 "Cov Assert RXT"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('AreEqual failed: %1 <> %2. %3', Expected, Actual, Msg);
    end;
}
