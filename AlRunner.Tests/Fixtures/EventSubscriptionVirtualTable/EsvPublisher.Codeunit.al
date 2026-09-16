// A codeunit that PUBLISHES an event and subscribes to nothing. It is the negative arm of
// the subscriber-codeunit filter: it is a codeunit in this same bundle, so only the
// subscription registry distinguishes it from "ESV Subscriber".
codeunit 70763 "ESV Publisher"
{
    [IntegrationEvent(false, false)]
    procedure OnEsvProbeEvent(Value: Integer)
    begin
    end;
}
