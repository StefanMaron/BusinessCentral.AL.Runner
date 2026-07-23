/// <summary>
/// Ordinary (SingleInstance=false, the default) codeunit — contrast case. Every
/// codeunit variable of this type must get its OWN fresh instance, unlike "SIC Single".
/// </summary>
codeunit 61302 "SIC Multi"
{
    var
        StoredValue: Integer;

    procedure SetValue(V: Integer)
    begin
        StoredValue := V;
    end;

    procedure GetValue(): Integer
    begin
        exit(StoredValue);
    end;
}
