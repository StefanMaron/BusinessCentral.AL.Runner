/// <summary>
/// The library's subscriber of "OnZ" calls a procedure that raises "OnY", whose subscriber calls a procedure
/// that raises "OnX", whose subscriber reaches the missing member: a test that raises "OnZ" reaches it
/// through three rounds of subscribers.
/// </summary>
codeunit 72211 "Round Helper"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Round Publisher", 'OnX', '', false, false)]
    local procedure HandleX()
    var
        Target: Codeunit "Round Target";
        Result: Integer;
    begin
        Result := Target.MissingR(1);
    end;

    procedure CallsRaiseX()
    var
        Publisher: Codeunit "Round Publisher";
    begin
        Publisher.RaiseX();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Round Publisher", 'OnY', '', false, false)]
    local procedure HandleY()
    begin
        CallsRaiseX();
    end;

    procedure CallsRaiseY()
    var
        Publisher: Codeunit "Round Publisher";
    begin
        Publisher.RaiseY();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Round Publisher", 'OnZ', '', false, false)]
    local procedure HandleZ()
    begin
        CallsRaiseY();
    end;
}
