codeunit 61000 "MD Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqual failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;

    procedure Contains(Haystack: Text; Needle: Text; Msg: Text)
    begin
        if not (StrPos(Haystack, Needle) > 0) then
            Error('Assert.Contains failed. Expected to find <%1>. Actual:<%2>. %3', Needle, Haystack, Msg);
    end;
}
