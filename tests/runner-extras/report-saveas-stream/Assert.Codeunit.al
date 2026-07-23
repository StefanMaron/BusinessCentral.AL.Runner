/// <summary>
/// Minimal assertion helper for this runner-extras app (own ID range).
/// </summary>
codeunit 60700 "RSS Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Assert.IsFalse failed. %1', Msg);
    end;

    procedure Contains(Haystack: Text; Needle: Text; Msg: Text)
    begin
        if StrPos(Haystack, Needle) = 0 then
            Error('Assert.Contains failed. Needle:<%1> not found. %2 Haystack(first 512):<%3>', Needle, Msg, CopyStr(Haystack, 1, 512));
    end;
}
