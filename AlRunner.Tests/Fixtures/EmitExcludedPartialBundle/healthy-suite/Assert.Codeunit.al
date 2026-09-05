codeunit 61350 "Emit Excl Part H Assert"
{
    procedure AreEqual(Expected: Integer; Actual: Integer; Msg: Text)
    begin
        if Expected <> Actual then
            Error('%1: expected %2, got %3', Msg, Expected, Actual);
    end;
}
