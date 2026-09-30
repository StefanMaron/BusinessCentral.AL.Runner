codeunit 50104 "Probe3415 4"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sales-Quote to Order", 'OnBeforeOnRun', '', false, false)]
    local procedure OnProbe4(var SalesHeader: Record "Sales Header")
    begin
        Error('PROBE-4');
    end;
}
