/// Tests for XmlDocument.Create, Add, GetRoot, ReadFrom, WriteTo,
/// GetChildNodes, SelectNodes, GetDeclaration, RemoveNodes.
codeunit 100201 "XmlDoc Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XmlDoc Src";

    // ── Create ────────────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_Create_DoesNotThrow()
    var
        Doc: XmlDocument;
    begin
        // [GIVEN/WHEN] XmlDocument.Create() is called
        // [THEN] no exception is raised
        Doc := Src.CreateDoc();
        Assert.IsTrue(true, 'XmlDocument.Create() must not throw');
    end;

    // ── Add + GetRoot ─────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_AddAndGetRoot_ReturnsElementName()
    begin
        // [GIVEN] a document with a root element named 'myRoot'
        // [WHEN] GetRoot is called
        // [THEN] the returned element's Name() is 'myRoot'
        Assert.AreEqual('myRoot', Src.AddAndGetRoot('myRoot'),
            'GetRoot must return the element previously added via Add()');
    end;

    [Test]
    procedure XmlDoc_AddAndGetRoot_DifferentName()
    begin
        Assert.AreEqual('doc', Src.AddAndGetRoot('doc'),
            'GetRoot name must match the name given to XmlElement.Create()');
    end;

    // ── ReadFrom ──────────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_ReadFrom_ParsesRootName()
    begin
        // [GIVEN] well-formed XML '<invoice><line/></invoice>'
        // [WHEN] ReadFrom parses it
        // [THEN] GetRoot.Name() returns 'invoice'
        Assert.AreEqual('invoice',
            Src.ReadFromAndGetRootName('<invoice><line/></invoice>'),
            'ReadFrom must parse the root element name correctly');
    end;

    [Test]
    procedure XmlDoc_ReadFrom_InvalidXml_Throws()
    begin
        // [GIVEN] malformed XML text
        // [WHEN] ReadFrom is called
        // [THEN] an error is raised
        asserterror Src.ReadFromInvalid('not xml at all <<<');
        Assert.ExpectedError('');
    end;

    // ── WriteTo ───────────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_WriteTo_ContainsElementName()
    begin
        // [GIVEN] a document with root element 'order'
        // [WHEN] WriteTo serializes it
        // [THEN] the result contains 'order'
        Assert.IsTrue(
            Src.WriteToText('order').Contains('order'),
            'WriteTo output must contain the root element name');
    end;

    [Test]
    procedure XmlDoc_WriteTo_NotEmpty()
    begin
        Assert.AreNotEqual('', Src.WriteToText('root'),
            'WriteTo must produce non-empty output for a document with a root element');
    end;

    // ── GetChildNodes ─────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_GetChildNodes_OneRootYieldsCount1()
    begin
        // [GIVEN] a document with exactly one root element
        // [WHEN] GetChildNodes is called
        // [THEN] the count is 1
        Assert.AreEqual(1, Src.GetChildNodesCount('root'),
            'A document with one root must have GetChildNodes().Count() = 1');
    end;

    // ── SelectNodes ───────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_SelectNodes_MatchesAll()
    begin
        // [GIVEN] XML with two <item> elements under <root>
        // [WHEN] SelectNodes with XPath '//item'
        // [THEN] count is 2
        Assert.AreEqual(2,
            Src.SelectNodesCount('<root><item/><item/></root>', '//item'),
            'XPath //item must match exactly 2 nodes');
    end;

    [Test]
    procedure XmlDoc_SelectNodes_NoMatch_ReturnsZero()
    begin
        Assert.AreEqual(0,
            Src.SelectNodesCount('<root><item/></root>', '//missing'),
            'XPath for non-existent element must return 0');
    end;

    // ── GetDeclaration ────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_GetDeclaration_ReturnsVersion()
    begin
        // [GIVEN] XML with <?xml version="1.0"?> declaration
        // [WHEN] GetDeclaration is called
        // [THEN] version is '1.0'
        Assert.AreEqual('1.0',
            Src.GetDeclarationVersion('<?xml version="1.0"?><root/>'),
            'GetDeclaration must return version 1.0 from the XML declaration');
    end;

    [Test]
    procedure XmlDoc_GetDeclaration_NonePresent_ReturnsEmpty()
    begin
        Assert.AreEqual('',
            Src.GetDeclarationVersion('<root/>'),
            'GetDeclaration must return empty when no declaration is present');
    end;

    // ── RemoveNodes ───────────────────────────────────────────────────────────

    [Test]
    procedure XmlDoc_RemoveNodes_ClearsChildren()
    begin
        // [GIVEN] an element with two children
        // [WHEN] RemoveNodes is called
        // [THEN] child count drops to 0
        Assert.AreEqual(0, Src.RemoveNodesCount(),
            'RemoveNodes must remove all child nodes');
    end;
}
