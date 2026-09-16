// This bundle's only subscriber. Its codeunit id is what the OTHER bundle filters on to
// prove it cannot see this bundle's subscriptions.
codeunit 70783 "EMB Subscriber A"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"EMB Publisher A", 'OnEmbProbeEventA', '', false, false)]
    local procedure HandleEmbProbeEventA(Value: Integer)
    begin
    end;
}
