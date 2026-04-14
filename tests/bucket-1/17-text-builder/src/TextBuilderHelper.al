codeunit 50117 "Text Builder Helper"
{
    procedure BuildGreeting(Name: Text; Title: Text): Text
    var
        TB: TextBuilder;
    begin
        TB.AppendLine('Dear ' + Title + ' ' + Name + ',');
        TB.AppendLine('Welcome to our service.');
        TB.Append('Best regards');
        exit(TB.ToText());
    end;

    procedure BuildList(Item1: Text; Item2: Text; Item3: Text): Text
    var
        TB: TextBuilder;
    begin
        TB.AppendLine('Items:');
        TB.AppendLine('- ' + Item1);
        TB.AppendLine('- ' + Item2);
        TB.Append('- ' + Item3);
        exit(TB.ToText());
    end;
}
