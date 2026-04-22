/// Tests that NavApp.GetResource with a TextEncoding parameter compiles and
/// runs without throwing in standalone mode (no-op stub contract).
codeunit 98102 "NavApp GetResource Encoding Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetResource_WithEncoding_IsNoOp()
    var
        Src: Codeunit "NavApp GetResource Encoding Src";
    begin
        // [GIVEN] A resource name and TextEncoding::UTF8
        // [WHEN]  NavApp.GetResource(ResourceName, InStream, TextEncoding::UTF8) is called
        // [THEN]  No exception — the 4-arg C# overload (with encoding) is a no-op
        Src.GetResourceWithEncoding('test.txt');
    end;

    [Test]
    procedure GetResource_WithoutEncoding_IsNoOp()
    var
        Src: Codeunit "NavApp GetResource Encoding Src";
    begin
        // [GIVEN] A resource name
        // [WHEN]  NavApp.GetResource(ResourceName, InStream) is called (existing 3-arg form)
        // [THEN]  No exception — regression guard for existing overload
        Src.GetResourceNoEncoding('test.txt');
    end;
}
