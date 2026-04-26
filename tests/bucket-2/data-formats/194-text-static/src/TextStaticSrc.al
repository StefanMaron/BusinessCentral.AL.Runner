/// Helper codeunit exercising the static Text.* built-in forms.
codeunit 60210 "TXST Src"
{
    procedure DelChrIt(v: Text; where: Text; chars: Text): Text
    begin
        exit(Text.DelChr(v, where, chars));
    end;

    procedure DelStrIt(v: Text; pos: Integer; count: Integer): Text
    begin
        exit(Text.DelStr(v, pos, count));
    end;

    procedure DelStrToEnd(v: Text; pos: Integer): Text
    begin
        // 2-arg overload: delete from pos to end.
        exit(Text.DelStr(v, pos));
    end;

    procedure InsStrIt(v: Text; ins: Text; pos: Integer): Text
    begin
        exit(Text.InsStr(v, ins, pos));
    end;

    procedure LowerIt(v: Text): Text
    begin
        exit(Text.LowerCase(v));
    end;

    procedure UpperIt(v: Text): Text
    begin
        exit(Text.UpperCase(v));
    end;

    procedure MaxStrLenIt(): Integer
    var
        s: Text[50];
    begin
        // MaxStrLen reports the declared max length of the argument expression.
        exit(Text.MaxStrLen(s));
    end;

    procedure StrLenIt(v: Text): Integer
    begin
        exit(Text.StrLen(v));
    end;

    procedure StrSubstNoOneArg(fmt: Text): Text
    begin
        exit(Text.StrSubstNo(fmt));
    end;

    procedure StrSubstNoTwoArg(fmt: Text; a: Text): Text
    begin
        exit(Text.StrSubstNo(fmt, a));
    end;

    procedure StrSubstNoThreeArg(fmt: Text; a: Text; b: Integer): Text
    begin
        exit(Text.StrSubstNo(fmt, a, b));
    end;
}
