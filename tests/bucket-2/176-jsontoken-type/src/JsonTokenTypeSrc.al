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
    procedure ObjectContains(JT: JsonToken; Key: Text): Boolean
    var
        JObj: JsonObject;
    begin
        JObj := JT.AsObject();
        exit(JObj.Contains(Key));
    end;

    /// Clone a token and verify the clone is independent.
    procedure CloneAndModify(JT: JsonToken): Text
    var
        Cloned: JsonToken;
        JVal: JsonValue;
    begin
        Cloned := JT.Clone();
        exit(Cloned.AsValue().AsText());
    end;

    /// Return the Path property of a token retrieved from an object.
    procedure GetPath(JT: JsonToken): Text
    begin
        exit(JT.Path);
    end;
}
