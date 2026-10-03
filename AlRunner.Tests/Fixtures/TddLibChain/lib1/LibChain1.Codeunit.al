/// <summary>Link 1 of the #5265 chain: "Go" calls "M1", which "Lib Chain 0" does not declare.</summary>
codeunit 72010 "Lib Chain 1"
{
    procedure Placeholder()
    begin
    end;

    procedure Go(): Integer
    var
        Previous: Codeunit "Lib Chain 0";
        Result: Integer;
    begin
        Result := Previous.M1(1);
        exit(Result);
    end;
}
