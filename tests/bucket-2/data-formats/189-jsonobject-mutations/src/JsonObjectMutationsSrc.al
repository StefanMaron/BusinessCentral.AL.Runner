/// Helper codeunit exercising JsonObject mutation and read operations:
/// Add, Contains, Get, Clone, Keys, AsToken.
codeunit 60150 "JOM Mut Src"
{
    procedure BuildMixedObject(): JsonObject
    var
        o: JsonObject;
    begin
        o.Add('name', 'Alice');
        o.Add('age', 30);
        o.Add('active', true);
        o.Add('rate', 3.14);
        exit(o);
    end;

    procedure HasKey(o: JsonObject; keyName: Text): Boolean
    begin
        exit(o.Contains(keyName));
    end;

    procedure GetText(o: JsonObject; keyName: Text): Text
    var
        t: JsonToken;
    begin
        o.Get(keyName, t);
        exit(t.AsValue().AsText());
    end;

    procedure GetInteger(o: JsonObject; keyName: Text): Integer
    var
        t: JsonToken;
    begin
        o.Get(keyName, t);
        exit(t.AsValue().AsInteger());
    end;

    procedure GetBoolean(o: JsonObject; keyName: Text): Boolean
    var
        t: JsonToken;
    begin
        o.Get(keyName, t);
        exit(t.AsValue().AsBoolean());
    end;

    procedure GetDecimal(o: JsonObject; keyName: Text): Decimal
    var
        t: JsonToken;
    begin
        o.Get(keyName, t);
        exit(t.AsValue().AsDecimal());
    end;

    procedure GetReturnsFalseForMissing(o: JsonObject; keyName: Text): Boolean
    var
        t: JsonToken;
    begin
        // AL Get returns Boolean — false when key absent.
        exit(o.Get(keyName, t));
    end;

    procedure CloneObject(o: JsonObject): JsonObject
    var
        clone: JsonToken;
    begin
        // JsonObject.Clone returns a JsonToken; unwrap back to JsonObject.
        clone := o.Clone();
        exit(clone.AsObject());
    end;

    procedure KeyCount(o: JsonObject): Integer
    var
        keys: List of [Text];
    begin
        keys := o.Keys();
        exit(keys.Count());
    end;

    procedure HasSpecificKey(o: JsonObject; keyName: Text): Boolean
    var
        keys: List of [Text];
    begin
        keys := o.Keys();
        exit(keys.Contains(keyName));
    end;

    procedure AsTokenRoundTrip_ReturnsObject(o: JsonObject; keyName: Text): Text
    var
        t: JsonToken;
        back: JsonObject;
        inner: JsonToken;
    begin
        t := o.AsToken();
        back := t.AsObject();
        back.Get(keyName, inner);
        exit(inner.AsValue().AsText());
    end;

    procedure ReplaceValue(var o: JsonObject; keyName: Text; v: Text)
    begin
        // AL allows Replace via Remove + Add.
        if o.Contains(keyName) then
            o.Remove(keyName);
        o.Add(keyName, v);
    end;
}
