interface "ILV Greeter"
{
    procedure Greet(): Text;
    procedure GreetName(Name: Text): Text;
}

codeunit 58300 "ILV Hello Greeter" implements "ILV Greeter"
{
    procedure Greet(): Text
    begin
        exit('Hello');
    end;

    procedure GreetName(Name: Text): Text
    begin
        exit('Hello ' + Name);
    end;
}

codeunit 58301 "ILV Goodbye Greeter" implements "ILV Greeter"
{
    procedure Greet(): Text
    begin
        exit('Goodbye');
    end;

    procedure GreetName(Name: Text): Text
    begin
        exit('Goodbye ' + Name);
    end;
}
