/// Helper codeunit exercising Text padding/trim/remove/replace methods.
codeunit 60080 "TPT Src"
{
    procedure PadLeftIt(v: Text; totalLength: Integer; padChar: Char): Text
    begin
        exit(v.PadLeft(totalLength, padChar));
    end;

    procedure PadLeft_SpaceDefault(v: Text; totalLength: Integer): Text
    begin
        exit(v.PadLeft(totalLength));
    end;

    procedure PadRightIt(v: Text; totalLength: Integer; padChar: Char): Text
    begin
        exit(v.PadRight(totalLength, padChar));
    end;

    procedure PadRight_SpaceDefault(v: Text; totalLength: Integer): Text
    begin
        exit(v.PadRight(totalLength));
    end;

    procedure RemoveFromStart(v: Text; startIndex: Integer): Text
    begin
        exit(v.Remove(startIndex));
    end;

    procedure RemoveCount(v: Text; startIndex: Integer; count: Integer): Text
    begin
        exit(v.Remove(startIndex, count));
    end;

    procedure ReplaceChars(v: Text; oldChar: Char; newChar: Char): Text
    begin
        exit(v.Replace(oldChar, newChar));
    end;

    procedure ReplaceStrings(v: Text; oldStr: Text; newStr: Text): Text
    begin
        exit(v.Replace(oldStr, newStr));
    end;

    procedure TrimIt(v: Text): Text
    begin
        exit(v.Trim());
    end;

    procedure TrimStartIt(v: Text): Text
    begin
        exit(v.TrimStart());
    end;

    procedure TrimEndIt(v: Text): Text
    begin
        exit(v.TrimEnd());
    end;
}
