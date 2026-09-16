// This bundle's only subscriber. Its codeunit id is what the OTHER bundle filters on to
// prove it cannot see this bundle's subscriptions.
codeunit 70803 "EMB Subscriber B"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"EMB Publisher B", 'OnEmbProbeEventB', '', false, false)]
    local procedure HandleEmbProbeEventB(Value: Integer)
    begin
    end;
}
