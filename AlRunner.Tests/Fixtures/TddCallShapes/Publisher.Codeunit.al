codeunit 65206 "Tdd Shape Publisher"
{
    procedure RaiseCounted()
    begin
        OnCounted();
    end;

    procedure RaiseOther()
    begin
        OnOther();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnCounted()
    begin
    end;

    [IntegrationEvent(false, false)]
    local procedure OnOther()
    begin
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Tdd Shape Publisher", 'OnCounted', '', false, false)]
    local procedure CountOnCounted()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.CountEvent(1);
    end;
}
