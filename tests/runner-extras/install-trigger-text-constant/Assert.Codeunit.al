// Standalone Assert codeunit — this suite must stand alone (README.md), it does not
// import from tests/al-language.
codeunit 62050 "ITTC Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Message: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('%1 (expected: %2, actual: %3)', Message, Format(Expected), Format(Actual));
    end;

    procedure IsTrue(Value: Boolean; Message: Text)
    begin
        if not Value then
            Error(Message);
    end;

    procedure IsFalse(Value: Boolean; Message: Text)
    begin
        if Value then
            Error(Message);
    end;
}
