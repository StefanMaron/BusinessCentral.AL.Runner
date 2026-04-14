codeunit 53202 "Greeting Service"
{
    procedure MakeGreeting(Greeter: Interface "IGreeter"; Name: Text): Text
    begin
        exit(Greeter.Greet(Name));
    end;
}
