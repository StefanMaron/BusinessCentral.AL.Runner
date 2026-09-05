// Standalone Assert codeunit — this suite must stand alone (tests/runner-extras/README.md),
// it does not import from tests/al-language.
codeunit 61103 "Ssd Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Assert.IsTrue failed. %1', Msg);
    end;

    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Assert.AreEqual failed. Expected <%1>, got <%2>. %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure Contains(Haystack: Text; Needle: Text; Msg: Text)
    begin
        if StrPos(Haystack, Needle) = 0 then
            Error('Assert.Contains failed. ''%1'' does not contain ''%2''. %3', Haystack, Needle, Msg);
    end;
}
