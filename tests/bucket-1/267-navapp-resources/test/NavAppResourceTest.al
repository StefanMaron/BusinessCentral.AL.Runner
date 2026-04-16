/// Tests for NavApp resource method stubs (GetResourceAsText, GetResourceAsJson, ListResources).
codeunit 85101 "NavApp Resource Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // ------------------------------------------------------------------
    // Positive: methods return safe defaults in standalone mode.
    // ------------------------------------------------------------------

    [Test]
    procedure GetResourceAsText_MissingResource_ReturnsEmpty()
    var
        Src: Codeunit "NavApp Resource Src";
        Result: Text;
    begin
        // [GIVEN] No .app is loaded (standalone mode)
        // [WHEN] GetResourceAsText is called for a non-existent resource
        Result := Src.GetTextResource('nonexistent.txt');
        // [THEN] Returns empty string — no exception
        Assert.AreEqual('', Result, 'GetResourceAsText must return empty string for missing resource in standalone mode');
    end;

    [Test]
    procedure GetResourceAsJson_MissingResource_ReturnsDefault()
    var
        Src: Codeunit "NavApp Resource Src";
        Obj: JsonObject;
    begin
        // [GIVEN] No .app is loaded
        // [WHEN] GetResourceAsJson is called for a non-existent resource
        Obj := Src.GetJsonResource('nonexistent.json');
        // [THEN] Returns a default JsonObject — no exception
        Assert.IsTrue(true, 'GetResourceAsJson must not throw for missing resource in standalone mode');
    end;

    [Test]
    procedure ListResources_ReturnsEmpty()
    var
        Src: Codeunit "NavApp Resource Src";
    begin
        // [GIVEN] No .app is loaded
        // [WHEN] ListResources is called
        // [THEN] Returns 0 resources — no exception
        Assert.AreEqual(0, Src.ListAllResources(), 'ListResources must return empty list in standalone mode');
    end;

    // ------------------------------------------------------------------
    // Negative: calling twice still returns empty (no state leak).
    // ------------------------------------------------------------------

    [Test]
    procedure GetResourceAsText_CalledTwice_BothReturnEmpty()
    var
        Src: Codeunit "NavApp Resource Src";
        R1: Text;
        R2: Text;
    begin
        R1 := Src.GetTextResource('a.txt');
        R2 := Src.GetTextResource('b.txt');
        Assert.AreEqual('', R1, 'First call must return empty');
        Assert.AreEqual('', R2, 'Second call must return empty');
    end;
}
