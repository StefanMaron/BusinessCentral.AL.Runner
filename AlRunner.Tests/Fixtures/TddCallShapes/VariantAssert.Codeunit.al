/// <summary>
/// The shape of Library Assert (130002): AreEqual takes its expected and actual values as Variant,
/// so the outer call does not fix the type of a missing procedure nested as an argument (#5146).
/// </summary>
codeunit 65200 "Tdd Shape Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure Between(Low: Variant; Value: Variant; High: Variant; Msg: Text)
    begin
    end;
}
