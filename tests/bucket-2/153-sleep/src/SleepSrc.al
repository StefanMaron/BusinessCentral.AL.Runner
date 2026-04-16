/// Helper codeunit that calls Sleep() — the AL built-in that pauses execution.
/// In the runner, Sleep must be a no-op so tests run without actually blocking.
codeunit 61210 "SLP Helper"
{
    procedure DoSleep(ms: Integer)
    begin
        Sleep(ms);
    end;

    procedure SleepAndReturn(ms: Integer): Text
    begin
        Sleep(ms);
        exit('done');
    end;
}
