codeunit 60800 "WLV Assert"
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

    procedure AreNotEqualInt(NotExpected: Integer; Actual: Integer; Msg: Text)
    begin
        if NotExpected = Actual then
            Error('Expected a value other than %1: %2', NotExpected, Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then Error('Expected false: %1', Msg);
    end;
}
