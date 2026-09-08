// Standalone Assert codeunit -- this suite must stand alone (README.md), it does not import
// from tests/al-language.
codeunit 65860 "Tcm Assert"
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
}
