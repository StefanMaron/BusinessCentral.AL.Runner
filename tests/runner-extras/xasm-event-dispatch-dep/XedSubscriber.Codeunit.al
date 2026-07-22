/// <summary>
/// The subscriber whose codeunit type ends up in TWO emitted assemblies during
/// a multi-bundle run (bundle 1's own emit + bundle 2's dep compile).
/// </summary>
codeunit 61211 "XED Subscriber"
{
    EventSubscriberInstance = StaticAutomatic;

    [EventSubscriber(ObjectType::Codeunit, Codeunit::"XED Publisher", 'OnPing', '', false, false)]
    local procedure HandlePing(var Count: Integer)
    begin
        Count += 1;
    end;
}
