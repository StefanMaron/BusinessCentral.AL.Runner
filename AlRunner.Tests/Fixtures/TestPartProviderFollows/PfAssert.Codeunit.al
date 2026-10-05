// Standalone Assert — this fixture stands alone and imports nothing.
codeunit 70960 "PF Assert"
{
    procedure AreEqual(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected <%1> but got <%2>: %3', Expected, Actual, Msg);
    end;

    procedure IsFalse(Condition: Boolean; Msg: Text)
    begin
        if Condition then
            Error('Expected false: %1', Msg);
    end;
}
