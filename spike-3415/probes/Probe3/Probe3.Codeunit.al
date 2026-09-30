codeunit 50103 "Probe3415 3"
{
    [EventSubscriber(ObjectType::Table, Database::"Sales Line", 'OnAfterValidateEvent', 'Quantity', false, false)]
    local procedure OnProbe3(var Rec: Record "Sales Line")
    begin
        Error('PROBE-3');
    end;
}
