/// <summary>#5271: calls "SameMissing" on another codeunit of its own library, which does not declare it.</summary>
codeunit 71971 "Lib Own Same"
{
    procedure Calc(): Integer
    var
        Other: Codeunit "Lib Own Other";
        Result: Integer;
    begin
        Result := Other.SameMissing(1);
        exit(Result);
    end;
}
