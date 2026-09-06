// Standalone Assert — this fixture must stand alone; it imports nothing.
codeunit 70622 "WQS Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected %1 but got %2: %3', Format(Expected), Format(Actual), Msg);
    end;

    procedure IsTrue(Condition: Boolean; Msg: Text)
    begin
        if not Condition then Error('Expected true: %1', Msg);
    end;
}
