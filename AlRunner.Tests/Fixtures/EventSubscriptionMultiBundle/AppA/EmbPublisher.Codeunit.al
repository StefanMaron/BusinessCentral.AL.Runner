// The event this bundle's own subscriber subscribes to. Nothing in the OTHER bundle
// subscribes to it, which is what makes the foreign-absence arm over there meaningful.
codeunit 70782 "EMB Publisher A"
{
    [IntegrationEvent(false, false)]
    procedure OnEmbProbeEventA(Value: Integer)
    begin
    end;
}
