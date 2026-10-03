/// <summary>
/// Names its publisher by a bare object id, which compiles and subscribes, but gives the call graph no
/// publisher name to read: the test raising the event is not annotated (#5161, #5245). `Codeunit::65206`
/// is a syntax error in AL, so this is the only id spelling there is.
/// </summary>
codeunit 65210 "Tdd Shape Id Subscriber"
{
    [EventSubscriber(ObjectType::Codeunit, 65206, 'OnById', '', false, false)]
    local procedure CountOnById()
    var
        Target: Codeunit "Tdd Shape Target Cu";
        Result: Integer;
    begin
        Result := Target.CountById(1);
    end;
}
