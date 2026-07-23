/// <summary>
/// SingleInstance=true codeunit: exactly one instance per session in real BC, so
/// StoredValue set through one handle/variable must be visible through a different
/// handle/variable of the same codeunit within the same test.
/// </summary>
codeunit 61301 "SIC Single"
{
    SingleInstance = true;

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
