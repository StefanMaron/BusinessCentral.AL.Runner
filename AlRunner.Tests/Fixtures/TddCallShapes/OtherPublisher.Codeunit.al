/// <summary>
/// A second publisher declaring the SAME event name as "Tdd Shape Publisher" (#5161): a subscriber
/// belongs to the publisher it names, not to every object with an event of that name.
/// </summary>
codeunit 65207 "Tdd Shape Other Publisher"
{
    procedure RaiseCounted()
    begin
        OnCounted();
    end;

    [IntegrationEvent(false, false)]
    local procedure OnCounted()
    begin
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Tdd Shape Other Publisher", 'OnCounted', '', false, false)]
    local procedure CountOnCounted()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.CountOtherEvent(1);
    end;
}
