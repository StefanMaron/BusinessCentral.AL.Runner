codeunit 83500 "Int ToText Src"
{
    procedure PositiveToText(): Text
    var
        n: Integer;
    begin
        n := 42;
        exit(n.ToText());
    end;

    procedure NegativeToText(): Text
    var
        n: Integer;
    begin
        n := -7;
        exit(n.ToText());
    end;

    procedure ZeroToText(): Text
    var
        n: Integer;
    begin
        n := 0;
        exit(n.ToText());
    end;

    procedure LargeToText(): Text
    var
        n: Integer;
    begin
        n := 1000000;
        exit(n.ToText());
    end;
}
