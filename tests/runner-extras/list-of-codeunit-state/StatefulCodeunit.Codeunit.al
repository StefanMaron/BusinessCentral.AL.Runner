/// <summary>
/// Plain stateful codeunit stored in a List of [Codeunit] by the tests.
/// </summary>
codeunit 63700 "LCS Stateful"
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
