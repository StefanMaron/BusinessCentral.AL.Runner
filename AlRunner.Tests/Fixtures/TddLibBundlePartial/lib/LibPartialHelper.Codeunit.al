/// <summary>The library object that calls the member the app does not declare.</summary>
codeunit 65530 "Lib Partial Helper"
{
    procedure Calc(): Integer
    var
        Loyalty: Codeunit "Lib Partial Loyalty";
        Result: Integer;
    begin
        Result := Loyalty.Missing(1);
        exit(Result);
    end;
}
