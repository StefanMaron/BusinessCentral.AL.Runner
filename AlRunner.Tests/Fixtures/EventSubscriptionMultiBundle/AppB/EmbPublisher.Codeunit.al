// The event this bundle's own subscriber subscribes to. Nothing in the OTHER bundle
// subscribes to it, which is what makes the foreign-absence arm over there meaningful.
codeunit 70802 "EMB Publisher B"
{
    [IntegrationEvent(false, false)]
    procedure OnEmbProbeEventB(Value: Integer)
    begin
    end;
}
