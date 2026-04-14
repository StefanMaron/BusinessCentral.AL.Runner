enum 56500 "EO Status"
{
    Extensible = true;
    value(0; " ") { }
    value(1; Open) { }
    value(2; Closed) { }
    value(3; Archived) { }
}

codeunit 56500 "EO Inspector"
{
    procedure CountOrdinals(): Integer
    var
        Ordinals: List of [Integer];
    begin
        Ordinals := Enum::"EO Status".Ordinals();
        exit(Ordinals.Count);
    end;

    procedure FirstOrdinal(): Integer
    var
        Ordinals: List of [Integer];
    begin
        Ordinals := Enum::"EO Status".Ordinals();
        exit(Ordinals.Get(1));
    end;
}
