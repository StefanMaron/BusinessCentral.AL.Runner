/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range).
/// </summary>
codeunit 65843 "RSM Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1> Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure Contains(Haystack: Text; Needle: Text; Msg: Text)
    begin
        if StrPos(Haystack, Needle) = 0 then
            Error('Assert.Contains failed. Expected to find:<%1> in:<%2>. %3', Needle, Haystack, Msg);
    end;
}
