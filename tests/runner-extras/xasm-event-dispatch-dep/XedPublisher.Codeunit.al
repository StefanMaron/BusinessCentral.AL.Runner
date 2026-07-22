/// <summary>
/// Codeunit-event publisher. FirePing raises OnPing; every registered
/// StaticAutomatic subscriber increments the by-ref counter, so the caller can
/// assert EXACTLY-ONCE dispatch (a cross-assembly duplicate registration would
/// either throw TargetException or double-increment).
/// </summary>
codeunit 61210 "XED Publisher"
{
    procedure FirePing(var Count: Integer)
    begin
        OnPing(Count);
    end;

    [IntegrationEvent(false, false)]
    local procedure OnPing(var Count: Integer)
    begin
    end;
}
