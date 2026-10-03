/// <summary>
/// #5264: the same three rounds with every subscriber in the test bundle, and the missing member on the
/// APP's codeunit, so the compile that generates it is the test bundle's and a second pass compiles it again.
/// </summary>
codeunit 72221 "Round Test Side"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Round Publisher", 'OnX2', '', false, false)]
    local procedure HandleX2()
    var
        Publisher: Codeunit "Round Publisher";
        Result: Integer;
    begin
        Result := Publisher.MissingApp(1);
    end;

    local procedure CallsRaiseX2()
    var
        Publisher: Codeunit "Round Publisher";
    begin
        Publisher.RaiseX2();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Round Publisher", 'OnY2', '', false, false)]
    local procedure HandleY2()
    begin
        CallsRaiseX2();
    end;

    local procedure CallsRaiseY2()
    var
        Publisher: Codeunit "Round Publisher";
    begin
        Publisher.RaiseY2();
    end;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Round Publisher", 'OnZ2', '', false, false)]
    local procedure HandleZ2()
    begin
        CallsRaiseY2();
    end;
}
