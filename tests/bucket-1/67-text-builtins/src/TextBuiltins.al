codeunit 50167 "Text Builtins Helper"
{
    procedure CallToLower(Input: Text): Text
    begin
        exit(Input.ToLower());
    end;

    procedure CallToUpper(Input: Text): Text
    begin
        exit(Input.ToUpper());
    end;

    procedure CallSubstring(Input: Text; StartPos: Integer; Length: Integer): Text
    begin
        exit(Input.Substring(StartPos, Length));
    end;

    procedure CallSubstringFromStart(Input: Text; StartPos: Integer): Text
    begin
        exit(Input.Substring(StartPos));
    end;

    procedure CallPadStr(Input: Text; Length: Integer): Text
    begin
        exit(PadStr(Input, Length));
    end;

    procedure CallPadStrRight(Input: Text; Length: Integer): Text
    begin
        exit(PadStr(Input, Length, ' '));
    end;

    procedure CallPadStrLeft(Input: Text; Length: Integer): Text
    begin
        exit(PadStr(Input, -Length, ' '));
    end;

    procedure CallPadStrChar(Input: Text; Length: Integer; Pad: Char): Text
    begin
        exit(PadStr(Input, Length, Pad));
    end;

    procedure CallStrPos(Haystack: Text; Needle: Text): Integer
    begin
        exit(StrPos(Haystack, Needle));
    end;

    procedure CallIndexOfAny(Input: Text; Chars: Text): Integer
    begin
        exit(Input.IndexOfAny(Chars));
    end;

    procedure CallIndexOfAnyStart(Input: Text; Chars: Text; StartIndex: Integer): Integer
    begin
        exit(Input.IndexOfAny(Chars, StartIndex));
    end;

    procedure CallStrCheckSum(Digits: Text; Weights: Text): Integer
    begin
        exit(StrCheckSum(Digits, Weights));
    end;

    procedure CallStrCheckSumSimple(Digits: Text): Integer
    begin
        exit(StrCheckSum(Digits));
    end;
}
