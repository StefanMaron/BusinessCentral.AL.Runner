// The only subscriber in this fixture: one codeunit-published event and one table-published
// trigger event. Both must appear in the Event Subscription virtual table.
codeunit 70764 "ESV Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"ESV Publisher", 'OnEsvProbeEvent', '', false, false)]
    local procedure HandleEsvProbeEvent(Value: Integer)
    begin
    end;

    [EventSubscriber(ObjectType::Table, Database::"ESV Watched", 'OnAfterInsertEvent', '', false, false)]
    local procedure HandleEsvWatchedInsert(var Rec: Record "ESV Watched"; RunTrigger: Boolean)
    begin
    end;
}
