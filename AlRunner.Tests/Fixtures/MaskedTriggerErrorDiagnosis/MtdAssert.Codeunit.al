// Standalone Assert — this fixture stands alone and imports nothing.
codeunit 70544 "MTD Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected <%1> but got <%2>: %3', Format(Expected), Format(Actual), Msg);
    end;
}
