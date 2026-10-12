codeunit 70007 "Probe Subscriber"
{
    var
        M: Codeunit "Missing Sub"; // declared by no app: --tdd generates it; no procedure mentions it

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Probe Publisher", 'OnThing', '', false, false)]
    local procedure HandleThing()
    begin
    end;
}
