codeunit 161001 "JSON Overloads Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JSON Overloads Src";

    // ── JsonObject.GetText(key, true) — key exists ────────────────────────────

    [Test]
    procedure GetText_WithRequireTrue_KeyExists_ReturnsValue()
    var
        Obj: JsonObject;
    begin
        Obj.Add('name', 'Alice');
        Assert.AreEqual('Alice', Src.GetTextWithBool(Obj, 'name', true),
            'GetText(key, true) must return the property value when key exists');
    end;

    // ── JsonObject.GetText(key, true) — key missing ───────────────────────────

    [Test]
    procedure GetText_WithRequireTrue_KeyMissing_ThrowsError()
    var
        Obj: JsonObject;
    begin
        asserterror Src.GetTextWithBool(Obj, 'missing', true);
        Assert.ExpectedError('');
    end;

    // ── JsonObject.GetText(key, false) — key missing returns empty ────────────

    [Test]
    procedure GetText_WithRequireFalse_KeyMissing_ReturnsEmpty()
    var
        Obj: JsonObject;
        Result: Text;
    begin
        Result := Src.GetTextWithBool(Obj, 'missing', false);
        Assert.AreEqual('', Result, 'GetText(key, false) must return empty text when key is missing');
    end;

    // ── JsonObject.GetText(key, false) — key exists returns value ─────────────

    [Test]
    procedure GetText_WithRequireFalse_KeyExists_ReturnsValue()
    var
        Obj: JsonObject;
    begin
        Obj.Add('city', 'Berlin');
        Assert.AreEqual('Berlin', Src.GetTextWithBool(Obj, 'city', false),
            'GetText(key, false) must return value when key exists');
    end;

    // ── JsonArray.GetObject(index) — returns object at index ─────────────────

    [Test]
    procedure GetObject_ByIndex_ReturnsCorrectObject()
    var
        Arr: JsonArray;
        Inner: JsonObject;
        Got: JsonObject;
        Token: JsonToken;
    begin
        Inner.Add('id', 42);
        Arr.Add(Inner);
        Got := Src.GetObjectByIndex(Arr, 0);
        Got.Get('id', Token);
        Assert.AreEqual(42, Token.AsValue().AsInteger(),
            'GetObject(0) must return the object stored at index 0');
    end;

    // ── JsonArray.GetObject(index) — out of bounds throws ────────────────────

    [Test]
    procedure GetObject_ByIndex_OutOfBounds_ThrowsError()
    var
        Arr: JsonArray;
    begin
        asserterror Src.GetObjectByIndex(Arr, 0);
        Assert.ExpectedError('');
    end;
}
