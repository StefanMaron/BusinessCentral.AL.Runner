codeunit 56670 VersionHelper
{
    procedure FormatVersion(Major: Integer; Minor: Integer; Build: Integer; Revision: Integer): Text
    var
        Result: Text;
    begin
        Result := StrSubstNo('%1.%2.%3.%4', Major, Minor, Build, Revision);
        exit(Result);
    end;

    procedure FormatMessage(Template: Text; Value: Integer): Text
    begin
        exit(StrSubstNo(Template, Value));
    end;
}
