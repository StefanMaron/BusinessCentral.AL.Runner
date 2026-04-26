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

    procedure SecondOrdinal(): Integer
    var
        Ordinals: List of [Integer];
    begin
        Ordinals := Enum::"EO Status".Ordinals();
        exit(Ordinals.Get(2));
    end;

    procedure ThirdOrdinal(): Integer
    var
        Ordinals: List of [Integer];
    begin
        Ordinals := Enum::"EO Status".Ordinals();
        exit(Ordinals.Get(3));
    end;

    procedure FourthOrdinal(): Integer
    var
        Ordinals: List of [Integer];
    begin
        Ordinals := Enum::"EO Status".Ordinals();
        exit(Ordinals.Get(4));
    end;

    procedure OrdinalsContains(Ordinal: Integer): Boolean
    var
        Ordinals: List of [Integer];
    begin
        Ordinals := Enum::"EO Status".Ordinals();
        exit(Ordinals.Contains(Ordinal));
    end;

    procedure OrdinalsInstanceSyntaxCount(): Integer
    var
        E: Enum "EO Status";
        Ordinals: List of [Integer];
    begin
        // Verify instance-variable syntax also works
        Ordinals := E.Ordinals();
        exit(Ordinals.Count);
    end;
}
