/// Source procedures for JsonValue extended-method coverage (issue #699).
/// Wraps each of the 14 gap methods so the test codeunit can call them.
codeunit 97703 "JVExt Src"
{
    procedure AsBigIntegerRoundTrip(): BigInteger
    var
        JV: JsonValue;
        bi: BigInteger;
    begin
        bi := 2000000000;
        JV.SetValue(bi);
        exit(JV.AsBigInteger());
    end;

    procedure AsByteRoundTrip(): Byte
    var
        JV: JsonValue;
        b: Byte;
    begin
        b := 200;
        JV.SetValue(b);
        exit(JV.AsByte());
    end;

    procedure AsCharRoundTrip(): Char
    var
        JV: JsonValue;
        c: Char;
    begin
        c := 65; // 'A'
        JV.SetValue(c);
        exit(JV.AsChar());
    end;

    procedure AsCodeRoundTrip(): Code[20]
    var
        JV: JsonValue;
        co: Code[20];
    begin
        co := 'MYCODE';
        JV.SetValue(co);
        exit(JV.AsCode());
    end;

    procedure AsDateRoundTrip(): Date
    var
        JV: JsonValue;
        d: Date;
    begin
        d := 20260101D;
        JV.SetValue(d);
        exit(JV.AsDate());
    end;

    procedure AsDateTimeSetsAndReturns(): Boolean
    var
        JV: JsonValue;
        dt: DateTime;
        result: DateTime;
    begin
        dt := CreateDateTime(20260101D, 000000T);
        JV.SetValue(dt);
        result := JV.AsDateTime();
        exit(result = dt);
    end;

    procedure AsDurationRoundTrip(): Duration
    var
        JV: JsonValue;
        dur: Duration;
    begin
        dur := 3600000; // 1 hour in ms
        JV.SetValue(dur);
        exit(JV.AsDuration());
    end;

    procedure AsOptionRoundTrip(): Integer
    var
        JV: JsonValue;
        opt: Option A, B, C;
    begin
        opt := opt::B; // = 1
        JV.SetValue(opt);
        exit(JV.AsOption());
    end;

    procedure AsTimeRoundTrip(): Time
    var
        JV: JsonValue;
        t: Time;
    begin
        t := 120000T;
        JV.SetValue(t);
        exit(JV.AsTime());
    end;

    procedure AsTokenIsValue(): Boolean
    var
        JV: JsonValue;
        JT: JsonToken;
    begin
        JV.SetValue(42);
        JT := JV.AsToken();
        exit(JT.IsValue());
    end;

    procedure CloneProducesEqualValue(): Boolean
    var
        JV: JsonValue;
        JC: JsonToken;
    begin
        JV.SetValue('original');
        JC := JV.Clone();
        exit(JC.AsValue().AsText() = 'original');
    end;

    procedure IsUndefined_Fresh(): Boolean
    var
        JV: JsonValue;
    begin
        exit(JV.IsUndefined());
    end;

    procedure IsUndefined_AfterSetValue(): Boolean
    var
        JV: JsonValue;
    begin
        JV.SetValue(42);
        exit(JV.IsUndefined());
    end;

    procedure IsUndefined_AfterSetValueToUndefined(): Boolean
    var
        JV: JsonValue;
    begin
        JV.SetValue(42);
        JV.SetValueToUndefined();
        exit(JV.IsUndefined());
    end;

    procedure PathReturnsText(): Text
    var
        JV: JsonValue;
    begin
        JV.SetValue(1);
        exit(JV.Path);
    end;

    /// Returns a different BigInteger value to test inequality
    procedure AsBigIntegerDifferentValue(): BigInteger
    var
        JV: JsonValue;
        bi: BigInteger;
    begin
        bi := 1;
        JV.SetValue(bi);
        exit(JV.AsBigInteger());
    end;

    procedure AsByteAltValue(): Byte
    var
        JV: JsonValue;
        b: Byte;
    begin
        b := 1;
        JV.SetValue(b);
        exit(JV.AsByte());
    end;
}
