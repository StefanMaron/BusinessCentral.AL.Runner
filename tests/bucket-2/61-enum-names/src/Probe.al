enum 56610 "EN Stage"
{
    Extensible = true;
    value(0; Draft) { }
    value(1; Review) { }
    value(2; Published) { }
}

codeunit 56610 "EN Probe"
{
    procedure NamesCount(): Integer
    var
        E: Enum "EN Stage";
        Names: List of [Text];
    begin
        E := E::Draft;
        Names := E.Names();
        exit(Names.Count);
    end;

    procedure NamesFirst(): Text
    var
        E: Enum "EN Stage";
        Names: List of [Text];
    begin
        E := E::Review;
        Names := E.Names();
        exit(Names.Get(1));
    end;
}
