// Tests for subscriber error propagation and asserterror interception.
// When a subscriber throws an error, it should propagate to the caller.
// The caller can catch it with asserterror.

codeunit 59970 "SE Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnProcess(Value: Integer)
    begin
    end;

    procedure Process(Value: Integer)
    begin
        OnProcess(Value);
    end;
}

codeunit 59971 "SE ErrorSubscriber"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"SE Publisher", OnProcess, '', true, true)]
    local procedure HandleProcess(Value: Integer)
    begin
        if Value < 0 then
            Error('Negative value not allowed: %1', Value);
    end;
}
