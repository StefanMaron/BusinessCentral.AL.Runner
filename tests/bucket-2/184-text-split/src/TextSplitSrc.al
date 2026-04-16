/// Helper codeunit exercising Text.Split (3 overloads).
codeunit 60090 "TXSP Src"
{
    procedure SplitCharJoin(v: Text; sep: Char): Text
    var
        parts: List of [Text];
        p: Text;
        out: Text;
    begin
        parts := v.Split(sep);
        foreach p in parts do
            out += p + '|';
        exit(out);
    end;

    procedure SplitCharCount(v: Text; sep: Char): Integer
    var
        parts: List of [Text];
    begin
        parts := v.Split(sep);
        exit(parts.Count());
    end;

    procedure SplitCharNth(v: Text; sep: Char; idx: Integer): Text
    var
        parts: List of [Text];
    begin
        parts := v.Split(sep);
        exit(parts.Get(idx));
    end;

    procedure SplitTextCount(v: Text; sep: Text): Integer
    var
        parts: List of [Text];
    begin
        parts := v.Split(sep);
        exit(parts.Count());
    end;

    procedure SplitTextNth(v: Text; sep: Text; idx: Integer): Text
    var
        parts: List of [Text];
    begin
        parts := v.Split(sep);
        exit(parts.Get(idx));
    end;

    procedure SplitMultipleSepsCount(v: Text): Integer
    var
        parts: List of [Text];
        seps: List of [Char];
    begin
        seps.Add(',');
        seps.Add(';');
        parts := v.Split(seps);
        exit(parts.Count());
    end;
}
