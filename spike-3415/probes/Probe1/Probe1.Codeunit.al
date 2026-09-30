codeunit 50101 "Probe3415 1"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Post", 'OnBeforePostSalesDoc', '', false, false)]
    local procedure OnProbe1(var SalesHeader: Record "Sales Header")
    begin
        Error('PROBE-1');
    end;
}
