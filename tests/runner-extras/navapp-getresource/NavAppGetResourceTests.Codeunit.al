/// <summary>
/// NavApp.GetResource / GetResourceAsText against this app's own packaged
/// resources (app.json "resourceFolders": ["res"]).
///
/// Positive: exact byte/text content round-trips through the InStream.
/// Negative: a missing resource name throws BC's real not-found error
/// ("A resource matching '{0}' could not be found in app '{1}'."), asserted
/// via its stable leading substring — never a silent default or a raw NRE.
/// </summary>
codeunit 61200 "NavApp GetResource Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "NGR Assert";

    [Test]
    procedure GetResource_TopLevelFile_ReturnsExactContent()
    var
        ResourceInStream: InStream;
        Content: Text;
    begin
        NavApp.GetResource('greeting.txt', ResourceInStream, TextEncoding::UTF8);
        ResourceInStream.ReadText(Content);
        Assert.AreEqual('Hello from packaged resource', Content, 'top-level resource content');
    end;

    [Test]
    procedure GetResource_NestedPath_ReturnsExactContent()
    var
        ResourceInStream: InStream;
        Content: Text;
    begin
        NavApp.GetResource('sub/nested.txt', ResourceInStream, TextEncoding::UTF8);
        ResourceInStream.ReadText(Content);
        Assert.AreEqual('nested-resource-content', Content, 'nested resource content');
    end;

    [Test]
    procedure GetResourceAsText_ReturnsExactContent()
    var
        Content: Text;
    begin
        Content := NavApp.GetResourceAsText('greeting.txt', TextEncoding::UTF8);
        Assert.AreEqual('Hello from packaged resource', Content, 'GetResourceAsText content');
    end;

    [Test]
    procedure GetResource_MissingName_ThrowsResourceNotFound()
    var
        ResourceInStream: InStream;
    begin
        asserterror NavApp.GetResource('missing.txt', ResourceInStream);
        Assert.ExpectedError('A resource matching ''missing.txt'' could not be found in app', GetLastErrorText());
    end;

    [Test]
    procedure GetResourceAsText_MissingName_ThrowsResourceNotFound()
    var
        Content: Text;
    begin
        asserterror Content := NavApp.GetResourceAsText('missing.txt');
        Assert.ExpectedError('A resource matching ''missing.txt'' could not be found in app', GetLastErrorText());
    end;
}
