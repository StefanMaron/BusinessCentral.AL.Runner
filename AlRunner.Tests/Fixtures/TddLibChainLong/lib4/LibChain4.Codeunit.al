/// <summary>Link 4 of the #5287 chain, one past the #5265 chain: "Go" calls "M4", which "Lib Chain 3" does not declare.</summary>
codeunit 72050 "Lib Chain 4"
{
    procedure Placeholder()
    begin
    end;

    procedure Go(): Integer
    var
        Previous: Codeunit "Lib Chain 3";
        Result: Integer;
    begin
        Result := Previous.M4(1);
        exit(Result);
    end;
}
