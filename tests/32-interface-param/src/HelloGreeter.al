codeunit 53201 "Hello Greeter" implements "IGreeter"
{
    procedure Greet(Name: Text): Text
    begin
        exit('Hello ' + Name);
    end;
}
