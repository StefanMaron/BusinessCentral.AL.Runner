codeunit 58100 "TBI Text Builtins Helper"
{
    procedure SelectStrToken(n: Integer; s: Text): Text
    begin
        exit(SelectStr(n, s));
    end;

    procedure IncStrValue(s: Text): Text
    begin
        exit(IncStr(s));
    end;

    procedure ConvertStrValue(s: Text; fromChars: Text; toChars: Text): Text
    begin
        exit(ConvertStr(s, fromChars, toChars));
    end;

    procedure CopyStrFromPos(s: Text; position: Integer): Text
    begin
        exit(CopyStr(s, position));
    end;

    procedure CopyStrSubstring(s: Text; position: Integer; length: Integer): Text
    begin
        exit(CopyStr(s, position, length));
    end;
}
