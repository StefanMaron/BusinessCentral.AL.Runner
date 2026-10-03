/// <summary>Link 3 of the #5265 chain: "Go" calls "M3", which "Lib Chain 2" does not declare.</summary>
codeunit 72030 "Lib Chain 3"
{
    procedure Placeholder()
    begin
    end;

    procedure Go(): Integer
    var
        Previous: Codeunit "Lib Chain 2";
        Result: Integer;
    begin
        Result := Previous.M3(1);
        exit(Result);
    end;
}
