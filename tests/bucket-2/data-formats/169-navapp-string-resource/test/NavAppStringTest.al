codeunit 169002 "NASR Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetResourceAsText_WithStringArg_ReturnsEmpty()
    var
        Src: Codeunit "NASR Source";
        Result: Text;
    begin
        // Positive: GetResourceAsText with a string-typed resource name compiles and
        // returns empty (no .app bundle in standalone mode).
        // This exercises the string overload of ALGetResourceAsText — issue #1107.
        Result := Src.GetResourceText('my-resource.json');
        Assert.AreEqual('', Result, 'No resource bundle in standalone mode — must return empty');
    end;
}
