// Standalone Assert: this fixture stands alone and imports nothing.
codeunit 73401 "BNR Assert"
{
    procedure AreEqual(Expected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(Expected) <> Format(Actual) then
            Error('Expected <%1> but got <%2>: %3', Expected, Actual, Msg);
    end;

    procedure AreNotEqual(NotExpected: Variant; Actual: Variant; Msg: Text)
    begin
        if Format(NotExpected) = Format(Actual) then
            Error('Did not expect <%1>: %2', Actual, Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Expected false: %1', Msg);
    end;
}
