/// Helper codeunit exercising Label string-method coverage.
/// Each procedure uses a Label with a canonical literal and invokes the
/// target method directly on the Label value — Labels in BC are string-like
/// values so these routing tests exercise the same NavText code paths as
/// plain Text but via the Label syntax.
codeunit 60120 "LBS Src"
{
    procedure Split_ByComma_Count(): Integer
    var
        lbl: Label 'a,b,c';
        parts: List of [Text];
    begin
        parts := lbl.Split(',');
        exit(parts.Count());
    end;

    procedure Substring_From(index: Integer): Text
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.Substring(index));
    end;

    procedure Substring_FromLength(index: Integer; length: Integer): Text
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.Substring(index, length));
    end;

    procedure PadLeftIt(total: Integer; padChar: Char): Text
    var
        lbl: Label '42';
    begin
        exit(lbl.PadLeft(total, padChar));
    end;

    procedure PadRightIt(total: Integer; padChar: Char): Text
    var
        lbl: Label '42';
    begin
        exit(lbl.PadRight(total, padChar));
    end;

    procedure RemoveFromStart(index: Integer): Text
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.Remove(index));
    end;

    procedure TrimStartIt(): Text
    var
        lbl: Label '   padded   ';
    begin
        exit(lbl.TrimStart());
    end;

    procedure TrimEndIt(): Text
    var
        lbl: Label '   padded   ';
    begin
        exit(lbl.TrimEnd());
    end;

    procedure LastIndexOfIt(needle: Text): Integer
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.LastIndexOf(needle));
    end;

    procedure IndexOfAnyIt(chars: Text): Integer
    var
        lbl: Label 'Hello World';
    begin
        exit(lbl.IndexOfAny(chars));
    end;
}
