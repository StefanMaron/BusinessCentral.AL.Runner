// Standalone Assert — this suite does not import from tests/al-language.
codeunit 65550 "PAS Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Expected true: %1', Msg);
    end;

    procedure ExpectedError(Expected: Text; Actual: Text)
    begin
        if StrPos(Actual, Expected) = 0 then
            Error('Expected an error containing ''%1'' but got ''%2''', Expected, Actual);
    end;
}
