/// <summary>
/// Subscribes to "Opt Publisher CEO".OnDoChoice with an Option-typed parameter.
/// Raises an error encoding the received option ordinal so the test can assert,
/// via asserterror, the exact value that was marshalled through the dispatcher.
/// This makes the subscriber's received value directly observable without
/// relying on SingleInstance state sharing.
/// </summary>
codeunit 60353 "Opt Subscriber CEO"
{
    [EventSubscriber(ObjectType::Codeunit, Codeunit::"Opt Publisher CEO", OnDoChoice, '', false, false)]
    local procedure OnDoChoice_Sub(Choice: Option First,Second,Third)
    begin
        Error('RECEIVED:%1', Choice);
    end;
}
