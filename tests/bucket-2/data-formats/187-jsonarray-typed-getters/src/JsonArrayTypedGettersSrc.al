/// Helper codeunit exercising JsonArray typed getters
/// (Get, plus typed extraction via JsonToken.AsValue / AsObject / AsArray).
///
/// Note: BC's strictly-typed JsonArray.GetBoolean / GetInteger / GetText /
/// GetDecimal / GetObject / GetArray overloads are BC 21+ APIs and are
/// not present in the AL 16.2 compiler bundled with the runner. In 16.2,
/// code typically goes through JsonArray.Get(idx, token) and then calls
/// AsValue().AsInteger() etc. This suite exercises that (equivalent) path
/// so the runner's BC-native JsonArray/JsonToken handling is proven.
codeunit 60130 "JATG Src"
{
    procedure BuildMixedArray(): JsonArray
    var
        a: JsonArray;
        inner: JsonArray;
        obj: JsonObject;
    begin
        a.Add(true);            // 0 -> Boolean
        a.Add(42);               // 1 -> Integer
        a.Add('hello');          // 2 -> Text
        a.Add(3.5);              // 3 -> Decimal
        obj.Add('k', 'v');
        a.Add(obj);              // 4 -> Object
        inner.Add('x');
        a.Add(inner);            // 5 -> Array
        exit(a);
    end;

    procedure GetIntViaToken(a: JsonArray; idx: Integer): Integer
    var
        t: JsonToken;
    begin
        a.Get(idx, t);
        exit(t.AsValue().AsInteger());
    end;

    procedure GetTextViaToken(a: JsonArray; idx: Integer): Text
    var
        t: JsonToken;
    begin
        a.Get(idx, t);
        exit(t.AsValue().AsText());
    end;

    procedure GetBooleanViaToken(a: JsonArray; idx: Integer): Boolean
    var
        t: JsonToken;
    begin
        a.Get(idx, t);
        exit(t.AsValue().AsBoolean());
    end;

    procedure GetDecimalViaToken(a: JsonArray; idx: Integer): Decimal
    var
        t: JsonToken;
    begin
        a.Get(idx, t);
        exit(t.AsValue().AsDecimal());
    end;

    procedure GetNestedObjectKey(a: JsonArray; idx: Integer; keyName: Text): Text
    var
        t: JsonToken;
        obj: JsonObject;
        inner: JsonToken;
    begin
        a.Get(idx, t);
        obj := t.AsObject();
        obj.Get(keyName, inner);
        exit(inner.AsValue().AsText());
    end;

    procedure GetNestedArrayCount(a: JsonArray; idx: Integer): Integer
    var
        t: JsonToken;
        inner: JsonArray;
    begin
        a.Get(idx, t);
        inner := t.AsArray();
        exit(inner.Count());
    end;

    procedure GetOutOfBoundsReturnsFalse(a: JsonArray; idx: Integer): Boolean
    var
        t: JsonToken;
    begin
        // AL's JsonArray.Get returns Boolean — false means out-of-range.
        exit(a.Get(idx, t));
    end;

    procedure GetInRangeReturnsTrue(a: JsonArray; idx: Integer): Boolean
    var
        t: JsonToken;
    begin
        exit(a.Get(idx, t));
    end;
}
