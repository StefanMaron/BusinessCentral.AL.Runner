codeunit 64452 "SRW Assert"
{
    procedure AreEqualText(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Expected ''%1'' but got ''%2'': %3', Expected, Actual, Msg);
    end;
}
