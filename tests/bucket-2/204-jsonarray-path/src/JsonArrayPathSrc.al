/// Exercises JsonArray.Path, AsToken, Clone, WriteTo — methods not yet
/// covered by earlier suites.
codeunit 60320 "JAP Src"
{
    procedure PathOfRootArray(): Text
    var
        a: JsonArray;
    begin
        a.Add(1);
        exit(a.Path);
    end;

    procedure PathOfNestedArray(): Text
    var
        obj: JsonObject;
        a: JsonArray;
        t: JsonToken;
    begin
        a.Add('x');
        obj.Add('items', a);
        obj.Get('items', t);
        exit(t.Path);
    end;

    procedure CloneIsIndependent(): Boolean
    var
        orig: JsonArray;
        cloned: JsonToken;
        clonedArr: JsonArray;
        t: JsonToken;
    begin
        orig.Add(42);
        cloned := orig.Clone();
        clonedArr := cloned.AsArray();
        clonedArr.Add(99);
        // orig should still have 1 element, cloned has 2.
        exit((orig.Count() = 1) and (clonedArr.Count() = 2));
    end;

    procedure AsTokenRoundTrip(): Integer
    var
        a: JsonArray;
        t: JsonToken;
        back: JsonArray;
    begin
        a.Add(1);
        a.Add(2);
        t := a.AsToken();
        back := t.AsArray();
        exit(back.Count());
    end;

    procedure WriteToContainsValues(): Boolean
    var
        a: JsonArray;
        outText: Text;
    begin
        a.Add(42);
        a.Add('hello');
        a.WriteTo(outText);
        exit(outText.Contains('42') and outText.Contains('hello'));
    end;
}
