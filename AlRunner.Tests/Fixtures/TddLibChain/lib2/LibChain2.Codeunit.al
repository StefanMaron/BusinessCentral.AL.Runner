/// <summary>Link 2 of the #5265 chain: "Go" calls "M2", which "Lib Chain 1" does not declare.</summary>
codeunit 72020 "Lib Chain 2"
{
    procedure Placeholder()
    begin
    end;

    procedure Go(): Integer
    var
        Previous: Codeunit "Lib Chain 1";
        Result: Integer;
    begin
        Result := Previous.M2(1);
        exit(Result);
    end;
}
