/// Helper codeunit exercising JsonArray.Count() — the method issue #468 says
/// is not supported at runtime.
codeunit 59350 "JAC Count Src"
{
    procedure GetCount(arr: JsonArray): Integer
    begin
        exit(arr.Count());
    end;

    /// Build an array with `n` text entries and return it.
    procedure BuildWith(n: Integer): JsonArray
    var
        arr: JsonArray;
        i: Integer;
    begin
        for i := 1 to n do
            arr.Add('item-' + Format(i));
        exit(arr);
    end;

    /// Mixed-type array — proves Count counts all tokens regardless of value type.
    procedure BuildMixed(): JsonArray
    var
        arr: JsonArray;
    begin
        arr.Add('text');
        arr.Add(42);
        arr.Add(true);
        arr.Add(3.14);
        exit(arr);
    end;

    /// Array containing a nested object and nested array — Count must still
    /// report the top-level length, not recurse into children.
    procedure BuildNested(): JsonArray
    var
        outer: JsonArray;
        inner: JsonArray;
        obj: JsonObject;
    begin
        inner.Add('a');
        inner.Add('b');
        obj.Add('key', 'value');

        outer.Add('first');
        outer.Add(inner);
        outer.Add(obj);
        exit(outer);
    end;
}
