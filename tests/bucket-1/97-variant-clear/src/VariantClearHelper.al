codeunit 297001 "Variant Clear Helper"
{
    procedure SetIntegerValue(var V: Variant; Value: Integer)
    begin
        V := Value;
    end;

    procedure ClearVariant(var V: Variant)
    begin
        Clear(V);
    end;

    procedure IsVariantInteger(V: Variant): Boolean
    begin
        exit(V.IsInteger());
    end;
}
