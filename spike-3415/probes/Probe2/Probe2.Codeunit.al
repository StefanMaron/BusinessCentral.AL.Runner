codeunit 50102 "Probe3415 2"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnAfterPostSalesDoc', '', false, false)]
    local procedure OnProbe2(var SalesHeader: Record "Sales Header")
    begin
        Error('PROBE-2');
    end;
}
