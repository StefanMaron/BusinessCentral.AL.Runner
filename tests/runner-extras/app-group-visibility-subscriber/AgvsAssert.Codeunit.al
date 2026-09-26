codeunit 66370 "AGVS Assert"
{
    procedure AreEqualText(Expected: Text; Actual: Text; Msg: Text)
    begin
        if Expected <> Actual then
            Error('Assert.AreEqualText failed. Expected:<%1>. Actual:<%2>. %3', Expected, Actual, Msg);
    end;
}
