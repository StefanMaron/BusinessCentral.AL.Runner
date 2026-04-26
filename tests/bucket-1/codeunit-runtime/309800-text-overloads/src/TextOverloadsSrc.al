/// Helper codeunit exercising the missing Text/Label/TextConst overloads:
///   IncStr(Text, BigInteger), MaxStrLen(Variant),
///   Split(Text), IndexOfAny(Text, Integer) on Text / Label / TextConst.
codeunit 309800 "TextOvl Src"
{
    var
        LabelVal: Label 'one,two,three';
        TextConstVal: Label 'hello world';

    // ------------------------------------------------------------------
    // IncStr — 2-arg: IncStr(s, stepCount)
    // ------------------------------------------------------------------

    procedure CallIncStrStep(s: Text; steps: BigInteger): Text
    begin
        exit(IncStr(s, steps));
    end;

    // ------------------------------------------------------------------
    // MaxStrLen — Variant overload (passes a variable, not a literal type)
    // ------------------------------------------------------------------

    procedure CallMaxStrLenVariant(): Integer
    var
        v: Text[42];
    begin
        exit(MaxStrLen(v));
    end;

    procedure CallMaxStrLenCode(): Integer
    var
        c: Code[15];
    begin
        exit(MaxStrLen(c));
    end;

    // ------------------------------------------------------------------
    // Text.Split(Text) — multi-char separator
    // ------------------------------------------------------------------

    procedure TextSplitTextCount(s: Text; sep: Text): Integer
    var
        Parts: List of [Text];
    begin
        Parts := s.Split(sep);
        exit(Parts.Count);
    end;

    procedure TextSplitTextNth(s: Text; sep: Text; n: Integer): Text
    var
        Parts: List of [Text];
    begin
        Parts := s.Split(sep);
        exit(Parts.Get(n));
    end;

    // ------------------------------------------------------------------
    // Text.IndexOfAny(Text, Integer) — 2-arg startIndex form
    // ------------------------------------------------------------------

    procedure TextIndexOfAnyStart(s: Text; chars: Text; startIdx: Integer): Integer
    begin
        exit(s.IndexOfAny(chars, startIdx));
    end;

    // ------------------------------------------------------------------
    // Label.Split(Text) — multi-char separator on a Label/TextConst
    // ------------------------------------------------------------------

    procedure LabelSplitTextCount(sep: Text): Integer
    var
        Parts: List of [Text];
    begin
        Parts := LabelVal.Split(sep);
        exit(Parts.Count);
    end;

    procedure LabelSplitTextNth(sep: Text; n: Integer): Text
    var
        Parts: List of [Text];
    begin
        Parts := LabelVal.Split(sep);
        exit(Parts.Get(n));
    end;

    // ------------------------------------------------------------------
    // Label.IndexOfAny(Text, Integer) — 2-arg startIndex form on Label
    // ------------------------------------------------------------------

    procedure LabelIndexOfAnyStart(chars: Text; startIdx: Integer): Integer
    begin
        exit(LabelVal.IndexOfAny(chars, startIdx));
    end;

    // ------------------------------------------------------------------
    // TextConst.Split(Text) — multi-char separator on TextConst
    // ------------------------------------------------------------------

    procedure TextConstSplitTextCount(sep: Text): Integer
    var
        Parts: List of [Text];
    begin
        Parts := TextConstVal.Split(sep);
        exit(Parts.Count);
    end;

    // ------------------------------------------------------------------
    // TextConst.IndexOfAny(Text, Integer) — 2-arg startIndex form on TextConst
    // ------------------------------------------------------------------

    procedure TextConstIndexOfAnyStart(chars: Text; startIdx: Integer): Integer
    begin
        exit(TextConstVal.IndexOfAny(chars, startIdx));
    end;
}
