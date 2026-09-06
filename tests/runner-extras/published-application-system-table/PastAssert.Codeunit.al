// Standalone Assert — this suite must stand alone, it does not import from tests/al-language.
codeunit 65570 "PAST Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure AreNotEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) = Format(Actual) then
            Error('Expected a value other than %1: %2', Format(Expected), Msg);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then Error('Expected true: %1', Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then Error('Expected false: %1', Msg);
    end;
}
