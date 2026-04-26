codeunit 58000 "Fluent Builder"
{
    var
        myValue: Integer;
        myName: Text;

    procedure SetValue(NewValue: Integer): Codeunit "Fluent Builder"
    begin
        myValue := NewValue;
        exit(this);
    end;

    procedure SetName(NewName: Text): Codeunit "Fluent Builder"
    begin
        myName := NewName;
        exit(this);
    end;

    procedure GetValue(): Integer
    begin
        exit(myValue);
    end;

    procedure GetName(): Text
    begin
        exit(myName);
    end;
}
