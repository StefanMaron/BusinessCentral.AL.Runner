// Standalone Assert — this suite does not import from tests/al-language.
codeunit 65960 "ETD Assert"
{
    procedure AreEqual(Expected: Boolean; Actual: Boolean; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected %1 but got %2: %3', Expected, Actual, Msg);
    end;
}
