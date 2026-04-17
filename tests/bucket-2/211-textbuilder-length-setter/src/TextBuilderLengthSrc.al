/// Exercises TextBuilder.Length setter (truncates the buffer).
codeunit 60380 "TBL Src"
{
    procedure Truncate(v: Text; newLength: Integer): Text
    var
        tb: TextBuilder;
    begin
        tb.Append(v);
        tb.Length := newLength;
        exit(tb.ToText());
    end;

    procedure TruncateToZero(v: Text): Text
    var
        tb: TextBuilder;
    begin
        tb.Append(v);
        tb.Length := 0;
        exit(tb.ToText());
    end;

    procedure LengthAfterSet(v: Text; newLength: Integer): Integer
    var
        tb: TextBuilder;
    begin
        tb.Append(v);
        tb.Length := newLength;
        exit(tb.Length);
    end;
}
