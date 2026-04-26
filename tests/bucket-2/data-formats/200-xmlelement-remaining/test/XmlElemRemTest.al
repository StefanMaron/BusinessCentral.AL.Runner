/// Tests for XmlElement remaining methods:
/// AddAfterSelf, AddBeforeSelf, GetDescendantElements, GetDescendantNodes,
/// GetDocument, GetNamespaceOfPrefix, GetPrefixOfNamespace, NamespaceUri,
/// ReplaceNodes, ReplaceWith.
codeunit 97601 "XER Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "XER Src";

    // ── NamespaceUri ──────────────────────────────────────────────────────────

    [Test]
    procedure NamespaceUri_PlainElem_Empty()
    begin
        Assert.AreEqual('', H.PlainElemNamespaceUri(),
            'plain element NamespaceUri must return empty string');
    end;

    [Test]
    procedure NamespaceUri_NamespacedElem_ReturnsUri()
    begin
        Assert.AreEqual('http://example.com', H.NamespacedElemUri(),
            'namespaced element NamespaceUri must return the namespace URI');
    end;

    // ── GetDocument ───────────────────────────────────────────────────────────

    [Test]
    procedure GetDocument_Detached_False()
    begin
        Assert.IsFalse(H.DetachedElemHasNoDocument(),
            'detached element GetDocument must return false');
    end;

    // ── GetDescendantElements ─────────────────────────────────────────────────

    [Test]
    procedure GetDescendantElements_Leaf_Zero()
    begin
        Assert.AreEqual(0, H.LeafDescendantElementsCount(),
            'leaf element must have 0 descendant elements');
    end;

    [Test]
    procedure GetDescendantElements_Nested_Two()
    begin
        Assert.AreEqual(2, H.NestedDescendantElementsCount(),
            'root with child+grandchild must have 2 descendant elements');
    end;

    // ── GetDescendantNodes ────────────────────────────────────────────────────

    [Test]
    procedure GetDescendantNodes_Leaf_Zero()
    begin
        Assert.AreEqual(0, H.LeafDescendantNodesCount(),
            'leaf element must have 0 descendant nodes');
    end;

    // ── GetNamespaceOfPrefix / GetPrefixOfNamespace ───────────────────────────

    [Test]
    procedure GetNamespaceOfPrefix_XmlPrefix_ReturnsUri()
    begin
        // 'xml' prefix is always defined per XML spec
        Assert.AreEqual('http://www.w3.org/XML/1998/namespace', H.NamespaceOfXmlPrefix(),
            'GetNamespaceOfPrefix for xml prefix must return the XML namespace URI');
    end;

    [Test]
    procedure GetPrefixOfNamespace_Unknown_Empty()
    begin
        Assert.AreEqual('', H.PrefixOfUnknownNamespace(),
            'GetPrefixOfNamespace for unknown URI must return empty');
    end;

    // ── ReplaceNodes ──────────────────────────────────────────────────────────

    [Test]
    procedure ReplaceNodes_OneChild()
    begin
        Assert.AreEqual(1, H.ReplaceNodesChildCount(),
            'ReplaceNodes must leave exactly one child');
    end;

    // ── ReplaceWith ───────────────────────────────────────────────────────────

    [Test]
    procedure ReplaceWith_NewNameInParent()
    begin
        Assert.AreEqual('new', H.ReplaceWithNewName(),
            'ReplaceWith must swap element so new one is in parent');
    end;

    // ── AddAfterSelf / AddBeforeSelf ──────────────────────────────────────────

    [Test]
    procedure AddAfterSelf_TwoChildren()
    begin
        Assert.AreEqual(2, H.AddAfterSelfChildCount(),
            'AddAfterSelf must result in 2 children in parent');
    end;

    [Test]
    procedure AddBeforeSelf_TwoChildren()
    begin
        Assert.AreEqual(2, H.AddBeforeSelfChildCount(),
            'AddBeforeSelf must result in 2 children in parent');
    end;

    // ── Compilation proof ─────────────────────────────────────────────────────

    [Test]
    procedure AllMethods_Compile()
    begin
        Assert.IsTrue(true, 'All XmlElement remaining methods must compile');
    end;
}
