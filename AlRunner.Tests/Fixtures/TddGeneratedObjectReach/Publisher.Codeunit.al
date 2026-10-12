codeunit 70008 "Probe Publisher"
{
    procedure Raise()
    begin
        OnThing();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnThing()
    begin
    end;
}
