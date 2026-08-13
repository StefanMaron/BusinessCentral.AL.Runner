codeunit 64300 "TSC Assert"
{
    procedure IsFalse(Value: Boolean; Msg: Text)
    begin
        if Value then
            Error('Assert.IsFalse failed. %1', Msg);
    end;

    procedure IsTrue(Value: Boolean; Msg: Text)
    begin
        if not Value then
            Error('Assert.IsTrue failed. %1', Msg);
    end;
}
