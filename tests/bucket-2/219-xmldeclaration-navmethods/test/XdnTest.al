/// Proving tests for XmlDeclaration navigation and tree-manipulation methods
/// (issue #779): WriteTo, AsXmlNode, GetParent, GetDocument, Remove,
/// AddAfterSelf, AddBeforeSelf, ReplaceWith, SelectNodes, SelectSingleNode.
codeunit 97908 "XDN Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XDN Src";

    // ── WriteTo ──────────────────────────────────────────────────────────────────

    [Test]
    procedure WriteTo_ContainsVersion()
    begin
        // [GIVEN] A declaration with version '1.0'
        // [WHEN]  WriteTo is called
        // [THEN]  The serialized text contains the version string
        Assert.IsTrue(
            Src.WriteToText('1.0', 'utf-8', '').Contains('1.0'),
            'WriteTo must contain the version');
    end;

    [Test]
    procedure WriteTo_ContainsEncoding()
    begin
        Assert.IsTrue(
            Src.WriteToText('1.0', 'utf-8', '').Contains('utf-8'),
            'WriteTo must contain the encoding');
    end;

    [Test]
    procedure WriteTo_DifferentVersions_ProduceDifferentOutput()
    begin
        // Negative trap: different versions must produce different output
        Assert.AreNotEqual(
            Src.WriteToText('1.0', '', ''),
            Src.WriteToText('1.1', '', ''),
            'WriteTo must reflect the actual version');
    end;

    // ── AsXmlNode ─────────────────────────────────────────────────────────────

    [Test]
    procedure AsXmlNode_IsXmlDeclaration()
    var
        Node: XmlNode;
    begin
        // [GIVEN] An XmlDeclaration
        // [WHEN]  AsXmlNode is called
        // [THEN]  The returned XmlNode wraps a declaration
        Node := Src.CreateAsXmlNode('1.0');
        Assert.IsTrue(Node.IsXmlDeclaration(), 'AsXmlNode().IsXmlDeclaration() must be true');
    end;

    [Test]
    procedure AsXmlNode_DifferentVersions_NotSameNode()
    var
        Node10: XmlNode;
        Node11: XmlNode;
        Decl10: XmlDeclaration;
        Decl11: XmlDeclaration;
    begin
        // Negative trap: two declarations with different versions are different nodes
        Decl10 := XmlDeclaration.Create('1.0', '', '');
        Decl11 := XmlDeclaration.Create('1.1', '', '');
        Node10 := Decl10.AsXmlNode();
        Node11 := Decl11.AsXmlNode();
        Assert.AreNotEqual(
            Node10.AsXmlDeclaration().Version,
            Node11.AsXmlDeclaration().Version,
            'AsXmlNode versions must differ');
    end;

    // ── GetParent ─────────────────────────────────────────────────────────────

    [Test]
    procedure GetParent_Detached_ReturnsFalse()
    begin
        // [GIVEN] A detached declaration
        // [WHEN]  GetParent is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.GetParentDetached(), 'Detached declaration has no parent');
    end;

    // ── GetDocument ───────────────────────────────────────────────────────────

    [Test]
    procedure GetDocument_Detached_ReturnsFalse()
    begin
        // [GIVEN] A detached declaration
        // [WHEN]  GetDocument is called
        // [THEN]  Returns false
        Assert.IsFalse(Src.GetDocumentDetached(), 'Detached declaration has no document');
    end;

    [Test]
    procedure GetDocument_InDocument_ReturnsTrue()
    begin
        // [GIVEN] A declaration set on a document
        // [WHEN]  GetDocument is called
        // [THEN]  Returns true
        Assert.IsTrue(Src.GetDocumentInDoc(), 'Declaration in document must return true for GetDocument');
    end;

    // ── Remove ────────────────────────────────────────────────────────────────

    [Test]
    procedure Remove_DetachesFromDocument()
    begin
        // [GIVEN] A declaration set on a document
        // [WHEN]  Remove is called
        // [THEN]  GetDeclaration on the document returns false
        Assert.IsFalse(Src.RemoveFromDoc(), 'After Remove, document must have no declaration');
    end;

    // ── AddAfterSelf / AddBeforeSelf ──────────────────────────────────────────

    [Test]
    procedure AddAfterSelf_NoThrow()
    begin
        // [WHEN]  AddAfterSelf is called (no-op for declaration nodes without XmlNode parent)
        // [THEN]  Does not throw
        Assert.AreEqual(1, Src.AddAfterSelfChildCount(), 'Document must still have one child element');
    end;

    [Test]
    procedure AddBeforeSelf_NoThrow()
    begin
        Assert.AreEqual(1, Src.AddBeforeSelfChildCount(), 'Document must still have one child element');
    end;

    // ── ReplaceWith ───────────────────────────────────────────────────────────

    [Test]
    procedure ReplaceWith_NoThrow()
    begin
        // [WHEN]  ReplaceWith is called on a declaration
        // [THEN]  Does not throw
        Assert.IsTrue(Src.ReplaceWithNoError(), 'ReplaceWith must not throw');
    end;

    // ── SelectNodes ───────────────────────────────────────────────────────────

    [Test]
    procedure SelectNodes_ReturnsEmptyList()
    begin
        // [GIVEN] A detached declaration
        // [WHEN]  SelectNodes is called
        // [THEN]  Returns an empty node list (declarations have no child nodes)
        Assert.AreEqual(0, Src.SelectNodesCount(), 'SelectNodes on declaration must return empty list');
    end;

    // ── SelectSingleNode ──────────────────────────────────────────────────────

    [Test]
    procedure SelectSingleNode_ReturnsFalse()
    begin
        // [GIVEN] A detached declaration
        // [WHEN]  SelectSingleNode is called
        // [THEN]  Returns false (no matching child)
        Assert.IsFalse(Src.SelectSingleNodeNotFound(), 'SelectSingleNode on declaration must return false');
    end;
}
