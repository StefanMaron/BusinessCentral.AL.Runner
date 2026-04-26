/// Tests for namespace-aware XPath overloads: Xml*.SelectNodes/SelectSingleNode(xpath, XmlNamespaceManager, ...).
/// All 20 overloads across 10 node types are backed by the same .NET XmlNode.SelectNodes/SelectSingleNode.
/// This suite proves XmlDocument and XmlElement (representative types) work correctly — issue #1371.
codeunit 309401 "XmlNs XPath Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XmlNs XPath Src";

    // ── XmlDocument.SelectNodes(xpath, XmlNamespaceManager, XmlNodeList) ─────

    [Test]
    procedure XmlDoc_SelectNodesNs_WithRegisteredPrefix_FindsAllNodes()
    var
        cnt: Integer;
    begin
        // 3 elements total (root + 2 children), XPath '//' finds all 3
        cnt := Src.XmlDoc_SelectNodesNs_CountMatch('ns', 'http://example.com', 'item');
        Assert.AreEqual(3, cnt, 'SelectNodes with registered prefix must find root + 2 children');
    end;

    [Test]
    procedure XmlDoc_SelectNodesNs_NonDefaultCount_ProvesMockIsntNoOp()
    var
        cnt: Integer;
    begin
        // The result must be 3, not 0 — a no-op mock returning 0 would fail
        cnt := Src.XmlDoc_SelectNodesNs_CountMatch('x', 'http://x.org', 'elem');
        Assert.AreNotEqual(0, cnt, 'SelectNodes must find the inserted elements — not zero');
    end;

    // ── XmlDocument.SelectSingleNode(xpath, XmlNamespaceManager, XmlNode) ────

    [Test]
    procedure XmlDoc_SelectSingleNodeNs_WithRegisteredPrefix_ReturnsCorrectLocalName()
    var
        localName: Text;
    begin
        localName := Src.XmlDoc_SelectSingleNodeNs_LocalName('ns', 'http://example.com', 'target');
        Assert.AreEqual('target', localName, 'SelectSingleNode must return element with matching local-name');
    end;

    [Test]
    procedure XmlDoc_SelectSingleNodeNs_UnknownPrefix_ReturnsFalse()
    var
        found: Boolean;
    begin
        found := Src.XmlDoc_SelectSingleNodeNs_UnknownPrefix_ReturnsFalse('http://example.com', 'item');
        Assert.IsFalse(found, 'SelectSingleNode with unregistered prefix must return false');
    end;

    // ── XmlElement.SelectNodes(xpath, XmlNamespaceManager, XmlNodeList) ──────

    [Test]
    procedure XmlElem_SelectNodesNs_WithRegisteredPrefix_FindsBothChildren()
    var
        cnt: Integer;
    begin
        cnt := Src.XmlElem_SelectNodesNs_CountChildren('ns', 'http://example.com', 'child');
        Assert.AreEqual(2, cnt, 'XmlElement.SelectNodes must find 2 namespace-qualified children');
    end;

    [Test]
    procedure XmlElem_SelectNodesNs_NonDefaultCount_ProvesMockIsntNoOp()
    var
        cnt: Integer;
    begin
        cnt := Src.XmlElem_SelectNodesNs_CountChildren('x', 'http://x.org', 'row');
        Assert.AreNotEqual(0, cnt, 'XmlElement.SelectNodes must find inserted children — not zero');
    end;

    // ── XmlElement.SelectSingleNode(xpath, XmlNamespaceManager, XmlNode) ─────

    [Test]
    procedure XmlElem_SelectSingleNodeNs_WithRegisteredPrefix_ReturnsCorrectLocalName()
    var
        localName: Text;
    begin
        localName := Src.XmlElem_SelectSingleNodeNs_LocalName('ns', 'http://example.com', 'child');
        Assert.AreEqual('child', localName, 'XmlElement.SelectSingleNode must return element with matching local-name');
    end;

    [Test]
    procedure XmlElem_SelectSingleNodeNs_DifferentPrefixesSameResult()
    var
        result1: Text;
        result2: Text;
    begin
        // Same URI, different prefix → same local-name result (namespace, not prefix, drives matching)
        result1 := Src.XmlElem_SelectSingleNodeNs_LocalName('ns', 'http://example.com', 'child');
        result2 := Src.XmlElem_SelectSingleNodeNs_LocalName('x', 'http://example.com', 'child');
        Assert.AreEqual(result1, result2, 'Different prefix for same URI must yield same local-name result');
    end;

    // ── XmlDeclaration.SelectNodes/SelectSingleNode with XmlNamespaceManager ─
    // Declarations have no child nodes — must return false without throwing.

    [Test]
    procedure XmlDecl_SelectNodesNs_ReturnsFalseWithoutThrowing()
    var
        found: Boolean;
    begin
        found := Src.XmlDecl_SelectNodesNs_ReturnsFalse();
        Assert.IsFalse(found, 'XmlDeclaration.SelectNodes with nsmgr must return false — no child nodes');
    end;

    [Test]
    procedure XmlDecl_SelectSingleNodeNs_ReturnsFalseWithoutThrowing()
    var
        found: Boolean;
    begin
        found := Src.XmlDecl_SelectSingleNodeNs_ReturnsFalse();
        Assert.IsFalse(found, 'XmlDeclaration.SelectSingleNode with nsmgr must return false — no child nodes');
    end;

    [Test]
    procedure XmlDecl_SelectNodesNs_AsStatement_NoThrow()
    var
        ok: Boolean;
    begin
        ok := Src.XmlDecl_SelectNodesNs_AsStatement_NoThrow();
        Assert.IsTrue(ok, 'XmlDeclaration.SelectNodes with nsmgr as statement must not throw');
    end;

    [Test]
    procedure XmlDecl_SelectSingleNodeNs_AsStatement_NoThrow()
    var
        ok: Boolean;
    begin
        ok := Src.XmlDecl_SelectSingleNodeNs_AsStatement_NoThrow();
        Assert.IsTrue(ok, 'XmlDeclaration.SelectSingleNode with nsmgr as statement must not throw');
    end;
}
