/// <summary>The subscriber of the table publisher's OnCounted (#5245).</summary>
codeunit 65209 "Tdd Shape Table Subscriber"
{
    [EventSubscriber(ObjectType::Table, Database::"Tdd Shape Publisher", 'OnCounted', '', false, false)]
    local procedure CountOnCounted()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.CountTableEvent(1);
    end;
}
