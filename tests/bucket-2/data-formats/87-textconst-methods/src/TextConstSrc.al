/// Exercises string-manipulation dot-methods on codeunit-level Label (TextConst) values.
codeunit 89000 "TCM Src"
{
    var
        HelloWorld: Label 'Hello, World!';
        Padded: Label '  padded  ';
        Multi: Label 'one,two,three';
        UpperVal: Label 'HELLO';
        LowerVal: Label 'world';

    procedure LabelContains(sub: Text): Boolean
    begin
        exit(HelloWorld.Contains(sub));
    end;

    procedure LabelStartsWith(prefix: Text): Boolean
    begin
        exit(HelloWorld.StartsWith(prefix));
    end;

    procedure LabelEndsWith(suffix: Text): Boolean
    begin
        exit(HelloWorld.EndsWith(suffix));
    end;

    procedure LabelIndexOf(sub: Text): Integer
    begin
        exit(HelloWorld.IndexOf(sub));
    end;

    procedure LabelLastIndexOf(sub: Text): Integer
    begin
        exit(HelloWorld.LastIndexOf(sub));
    end;

    procedure LabelIndexOfAny(chars: Text): Integer
    begin
        exit(HelloWorld.IndexOfAny(chars));
    end;

    procedure LabelIndexOfAnyFrom(chars: Text; startIndex: Integer): Integer
    begin
        exit(HelloWorld.IndexOfAny(chars, startIndex));
    end;

    procedure LabelSubstring(start: Integer; len: Integer): Text
    begin
        exit(HelloWorld.Substring(start, len));
    end;

    procedure LabelSubstringFrom(start: Integer): Text
    begin
        exit(HelloWorld.Substring(start));
    end;

    procedure LabelToLower(): Text
    begin
        exit(UpperVal.ToLower());
    end;

    procedure LabelToUpper(): Text
    begin
        exit(LowerVal.ToUpper());
    end;

    procedure LabelTrim(): Text
    begin
        exit(Padded.Trim());
    end;

    procedure LabelTrimStart(): Text
    begin
        exit(Padded.TrimStart());
    end;

    procedure LabelTrimEnd(): Text
    begin
        exit(Padded.TrimEnd());
    end;

    procedure LabelPadLeft(totalWidth: Integer): Text
    begin
        exit(LowerVal.PadLeft(totalWidth));
    end;

    procedure LabelPadRight(totalWidth: Integer): Text
    begin
        exit(LowerVal.PadRight(totalWidth));
    end;

    procedure LabelReplace(oldVal: Text; newVal: Text): Text
    begin
        exit(HelloWorld.Replace(oldVal, newVal));
    end;

    procedure LabelRemove(startIndex: Integer; count: Integer): Text
    begin
        exit(HelloWorld.Remove(startIndex, count));
    end;

    procedure LabelSplitCount(sep: Text): Integer
    var
        Parts: List of [Text];
    begin
        Parts := Multi.Split(sep);
        exit(Parts.Count);
    end;
}
