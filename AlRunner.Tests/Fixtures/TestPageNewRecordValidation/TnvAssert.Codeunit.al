// Standalone Assert — this fixture stands alone and imports nothing.
codeunit 70404 "TNV Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected <%1> but got <%2>: %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure AreNotEqual(NotExpected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(NotExpected) = Format(Actual) then
            Error('Expected any value except <%1>, but got it: %2', Format(NotExpected), Msg);
    end;
}
