codeunit 50420 "Try Caller"
{
    procedure ProbeSuccess(): Boolean
    var
        Ok: Boolean;
    begin
        Ok := AlwaysSucceeds();
        exit(Ok);
    end;

    procedure ProbeFailure(): Boolean
    var
        Ok: Boolean;
    begin
        Ok := AlwaysFails();
        exit(Ok);
    end;

    procedure ProbeFailureIgnoringResult(): Text
    begin
        if AlwaysFails() then
            exit('unexpected-success');
        exit('handled-failure');
    end;

    [TryFunction]
    local procedure AlwaysSucceeds()
    begin
        // No Error — TryFunction should return true.
    end;

    [TryFunction]
    local procedure AlwaysFails()
    begin
        Error('boom');
    end;
}
