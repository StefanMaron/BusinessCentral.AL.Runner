/// Helper codeunit exercising JsonArray mutation methods:
/// Add, Set, Insert, RemoveAt, IndexOf.
codeunit 60140 "JAM Src"
{
    procedure BuildThreeElementArray(): JsonArray
    var
        a: JsonArray;
    begin
        a.Add(42);
        a.Add('hello');
        a.Add(true);
        exit(a);
    end;

    procedure Count(a: JsonArray): Integer
    begin
        exit(a.Count());
    end;

    procedure FirstInt(a: JsonArray): Integer
    var
        t: JsonToken;
    begin
        a.Get(0, t);
        exit(t.AsValue().AsInteger());
    end;

    procedure SecondText(a: JsonArray): Text
    var
        t: JsonToken;
    begin
        a.Get(1, t);
        exit(t.AsValue().AsText());
    end;

    procedure ThirdBool(a: JsonArray): Boolean
    var
        t: JsonToken;
    begin
        a.Get(2, t);
        exit(t.AsValue().AsBoolean());
    end;

    procedure SetItem_Int(var a: JsonArray; idx: Integer; v: Integer)
    begin
        a.Set(idx, v);
    end;

    procedure InsertInt(var a: JsonArray; idx: Integer; v: Integer)
    begin
        a.Insert(idx, v);
    end;

    procedure RemoveAt(var a: JsonArray; idx: Integer)
    begin
        a.RemoveAt(idx);
    end;

    procedure IntAt(a: JsonArray; idx: Integer): Integer
    var
        t: JsonToken;
    begin
        a.Get(idx, t);
        exit(t.AsValue().AsInteger());
    end;

    procedure AddJsonObject(): JsonArray
    var
        a: JsonArray;
        obj: JsonObject;
    begin
        obj.Add('k', 'v');
        a.Add(obj);
        exit(a);
    end;

    procedure NestedObjectKey(a: JsonArray; idx: Integer; keyName: Text): Text
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

    procedure IndexOfInt(a: JsonArray; v: Integer): Integer
    begin
        exit(a.IndexOf(v));
    end;
}
