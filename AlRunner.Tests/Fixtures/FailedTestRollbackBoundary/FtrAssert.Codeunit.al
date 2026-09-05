// Standalone Assert codeunit — this fixture must stand alone, it does not import from
// tests/al-language or tests/runner-extras.
codeunit 70300 "FTR Assert"
{
    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then
            Error('Expected true: %1', Msg);
    end;

    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;
}
