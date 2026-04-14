codeunit 53203 "Greeting Runner"
{
    var
        Greeter: Interface "IGreeter";

    procedure SetGreeter(NewGreeter: Interface "IGreeter")
    begin
        Greeter := NewGreeter;
    end;

    procedure RunGreeting(Name: Text): Text
    begin
        exit(Greeter.Greet(Name));
    end;
}
