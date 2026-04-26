/// Helper codeunit exercising JsonToken type-checking:
/// IsArray, IsObject, IsValue, AsArray, AsObject, AsValue, Clone, Path.
codeunit 97100 "JTT Src"
{
    /// Returns 'array', 'object', 'value', or 'unknown' based on token type.
    procedure TokenKind(JT: JsonToken): Text
    begin
        if JT.IsArray() then
            exit('array');
        if JT.IsObject() then
            exit('object');
        if JT.IsValue() then
            exit('value');
        exit('unknown');
    end;

    /// Extract an integer from a value token via AsValue().
    procedure ExtractInt(JT: JsonToken): Integer
    begin
        exit(JT.AsValue().AsInteger());
    end;

    /// Extract a text string from a value token via AsValue().
    procedure ExtractText(JT: JsonToken): Text
    begin
        exit(JT.AsValue().AsText());
    end;

    /// Return the count of the array obtained via AsArray().
    procedure ArrayCount(JT: JsonToken): Integer
    var
        JArr: JsonArray;
    begin
        JArr := JT.AsArray();
        exit(JArr.Count());
    end;

    /// Return whether the object obtained via AsObject() contains the given key.
    procedure ObjectContains(JT: JsonToken; PropName: Text): Boolean
    var
        JObj: JsonObject;
    begin
        JObj := JT.AsObject();
        exit(JObj.Contains(PropName));
    end;

    /// Clone a token and verify the clone has the same value.
    procedure CloneAndRead(JT: JsonToken): Text
    var
        Cloned: JsonToken;
    begin
        Cloned := JT.Clone();
        exit(Cloned.AsValue().AsText());
    end;
}
