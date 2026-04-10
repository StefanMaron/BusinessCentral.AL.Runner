codeunit 50106 "Greeter"
{
    procedure Greet(Name: Text[100]): Text[250]
    begin
        if Name = '' then
            Error('Name must not be empty');
        exit('Hello, ' + Name + '!');
    end;
}
