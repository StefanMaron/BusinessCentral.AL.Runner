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

    procedure NamesSecond(): Text
    var
        E: Enum "EN Stage";
        Names: List of [Text];
    begin
        Names := Enum::"EN Stage".Names();
        exit(Names.Get(2));
    end;

    procedure NamesThird(): Text
    var
        E: Enum "EN Stage";
        Names: List of [Text];
    begin
        Names := Enum::"EN Stage".Names();
        exit(Names.Get(3));
    end;

    procedure NamesContains(SearchName: Text): Boolean
    var
        E: Enum "EN Stage";
        Names: List of [Text];
    begin
        Names := E.Names();
        exit(Names.Contains(SearchName));
    end;

    procedure NamesTypeQualifierCount(): Integer
    var
        Names: List of [Text];
    begin
        // Verify static type-qualifier syntax Enum::"EN Stage".Names() also works
        Names := Enum::"EN Stage".Names();
        exit(Names.Count);
    end;
}
