/// <summary>The library object that calls the member the app does not declare.</summary>
codeunit 65560 "Lib Overload Helper"
{
    procedure Calc(): Integer
    var
        Loyalty: Codeunit "Lib Overload Loyalty";
        Result: Integer;
    begin
        Result := Loyalty.Missing(1);
        exit(Result);
    end;
}
