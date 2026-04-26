/// Exercises JsonValue — SetValue + typed As* extraction,
/// IsUndefined, Path, AsToken, Clone.
codeunit 60350 "JVX Src"
{
    procedure SetAndGetInteger(): Integer
    var
        v: JsonValue;
    begin
        v.SetValue(42);
        exit(v.AsInteger());
    end;

    procedure SetAndGetText(): Text
    var
        v: JsonValue;
    begin
        v.SetValue('hello');
        exit(v.AsText());
    end;

    procedure SetAndGetBoolean(): Boolean
    var
        v: JsonValue;
    begin
        v.SetValue(true);
        exit(v.AsBoolean());
    end;

    procedure SetAndGetDecimal(): Decimal
    var
        v: JsonValue;
    begin
        v.SetValue(3.14);
        exit(v.AsDecimal());
    end;

    procedure IsUndefined_Default(): Boolean
    var
        v: JsonValue;
    begin
        exit(v.IsUndefined());
    end;

    procedure IsUndefined_AfterSet(): Boolean
    var
        v: JsonValue;
    begin
        v.SetValue(1);
        exit(v.IsUndefined());
    end;

    procedure PathOfNestedValue(): Text
    var
        obj: JsonObject;
        t: JsonToken;
    begin
        obj.Add('score', 99);
        obj.Get('score', t);
        exit(t.Path);
    end;

    procedure AsTokenRoundTrip(): Integer
    var
        v: JsonValue;
        t: JsonToken;
    begin
        v.SetValue(77);
        t := v.AsToken();
        exit(t.AsValue().AsInteger());
    end;

    procedure CloneIsIndependent(): Boolean
    var
        orig: JsonValue;
        cloned: JsonToken;
        clonedVal: JsonValue;
    begin
        orig.SetValue(42);
        cloned := orig.Clone();
        clonedVal := cloned.AsValue();
        clonedVal.SetValue(99);
        exit((orig.AsInteger() = 42) and (clonedVal.AsInteger() = 99));
    end;
}
