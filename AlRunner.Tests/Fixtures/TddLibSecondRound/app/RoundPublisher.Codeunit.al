/// <summary>
/// The publisher. "RaiseZ", "RaiseY" and "RaiseX" raise "OnZ", "OnY" and "OnX"; "Quiet" raises an event nobody
/// subscribes to. The "...2" procedures and events are the same three rounds for subscribers in the test bundle.
/// </summary>
codeunit 72200 "Round Publisher"
{
    procedure Placeholder()
    begin
    end;

    procedure RaiseX()
    begin
        OnX();
    end;

    procedure RaiseX2()
    begin
        OnX2();
    end;

    procedure RaiseY2()
    begin
        OnY2();
    end;

    procedure RaiseZ2()
    begin
        OnZ2();
    end;

    procedure RaiseZ()
    begin
        OnZ();
    end;

    procedure RaiseY()
    begin
        OnY();
    end;

    procedure Quiet()
    begin
        OnQuiet();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnX()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnY()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnQuiet()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnZ()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnX2()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnY2()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnZ2()
    begin
    end;
}
