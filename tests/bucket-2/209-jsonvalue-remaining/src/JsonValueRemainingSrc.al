/// Exercises remaining JsonValue typed conversions that compile in AL 16.2:
/// AsBigInteger, AsCode, AsOption, AsDate, AsTime, AsDateTime.
/// AsByte/AsChar/AsDuration/SetValueToUndefined may be BC 21+ only.
codeunit 60360 "JVR Src"
{
    procedure AsBigInteger(): BigInteger
    var
        v: JsonValue;
    begin
        v.SetValue(1234567890);
        exit(v.AsBigInteger());
    end;

    procedure AsCode(): Code[50]
    var
        v: JsonValue;
    begin
        v.SetValue('ABC-123');
        exit(v.AsCode());
    end;

    procedure AsOption(): Integer
    var
        v: JsonValue;
    begin
        v.SetValue(2);
        exit(v.AsOption());
    end;

    procedure AsDate(): Date
    var
        v: JsonValue;
    begin
        v.SetValue(DMY2Date(15, 6, 2024));
        exit(v.AsDate());
    end;

    procedure AsTime(): Time
    var
        v: JsonValue;
    begin
        v.SetValue(120000T);
        exit(v.AsTime());
    end;

    procedure AsDateTime(): DateTime
    var
        v: JsonValue;
    begin
        v.SetValue(CurrentDateTime());
        exit(v.AsDateTime());
    end;

    procedure AsByte(): Byte
    var
        v: JsonValue;
    begin
        v.SetValue(42);
        exit(v.AsByte());
    end;

    procedure AsChar(): Char
    var
        v: JsonValue;
    begin
        v.SetValue(65);
        exit(v.AsChar());
    end;

    procedure AsDuration(): Duration
    var
        v: JsonValue;
    begin
        v.SetValue(60000);
        exit(v.AsDuration());
    end;
}
