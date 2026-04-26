// Supporting codeunit that writes a known value so the test can verify
// the session codeunit actually ran (not a no-op).
codeunit 1312001 "StartSession2Arg Counter"
{
    trigger OnRun()
    begin
        // Increment the shared counter so the caller can detect execution.
        Counter += 1;
    end;

    var
        Counter: Integer;

    procedure GetCount(): Integer
    begin
        exit(Counter);
    end;
}

codeunit 1312002 "StartSession2Arg Api"
{
    procedure TryStartSessionNoCompany(var SessionId: Integer): Boolean
    begin
        // 2-arg form: StartSession(var SessionId, CodeunitID) — no Company arg.
        exit(StartSession(SessionId, Codeunit::"StartSession2Arg Worker"));
    end;

    procedure TryStartSessionMissingCU(var SessionId: Integer): Boolean
    begin
        // Negative test: pass a user-range codeunit ID that does not exist in the assembly
        // (50xxx range). The runner throws for missing user codeunits; StartSession traps
        // the error and returns false.
        exit(StartSession(SessionId, 59999));
    end;
}

codeunit 1312003 "StartSession2Arg Worker"
{
    trigger OnRun()
    begin
        // No-op worker dispatched via 2-arg StartSession.
    end;
}
