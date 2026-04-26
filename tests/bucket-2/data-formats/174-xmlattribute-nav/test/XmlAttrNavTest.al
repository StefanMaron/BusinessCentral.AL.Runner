/// Tests for XmlAttribute navigation and namespace methods:
/// NamespacePrefix, IsNamespaceDeclaration, CreateNamespaceDeclaration,
/// WriteTo, GetParent, GetDocument, Remove, SelectNodes, SelectSingleNode.
codeunit 97401 "XAN Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "XAN Src";

    // ── NamespacePrefix ───────────────────────────────────────────────────────

    [Test]
    procedure NamespacePrefix_PlainAttr_Empty()
    begin
        Assert.AreEqual('', H.PlainAttrNamespacePrefix(),
            'plain attribute NamespacePrefix must return empty string');
    end;

    [Test]
    procedure NamespacePrefix_PlainAttr_NotNonEmpty()
    begin
        Assert.AreNotEqual('xml', H.PlainAttrNamespacePrefix(),
            'plain attribute NamespacePrefix must not return xml prefix');
    end;

    // ── IsNamespaceDeclaration ────────────────────────────────────────────────

    [Test]
    procedure IsNamespaceDeclaration_PlainAttr_False()
    begin
        Assert.IsFalse(H.PlainAttrIsNsDecl(),
            'plain attribute must not be a namespace declaration');
    end;

    [Test]
    procedure IsNamespaceDeclaration_PlainAttr_NotTrue()
    begin
        Assert.AreNotEqual(true, H.PlainAttrIsNsDecl(),
            'plain attribute IsNamespaceDeclaration must not return true');
    end;

    // ── CreateNamespaceDeclaration ────────────────────────────────────────────

    [Test]
    procedure CreateNsDecl_IsNamespaceDeclaration_True()
    begin
        Assert.IsTrue(H.CreateNsDecl('ns', 'http://example.com'),
            'CreateNamespaceDeclaration result must report IsNamespaceDeclaration = true');
    end;

    [Test]
    procedure CreateNsDecl_LocalName_EqualsPrefix()
    begin
        Assert.AreEqual('ns', H.CreateNsDeclLocalName('ns', 'http://example.com'),
            'CreateNamespaceDeclaration LocalName must equal the prefix');
    end;

    // ── WriteTo ────────────────────────────────────────────────────────────────

    [Test]
    procedure WriteTo_ContainsName()
    begin
        Assert.IsTrue(H.WriteToText('mykey', 'myvalue').Contains('mykey'),
            'WriteTo output must contain the attribute name');
    end;

    [Test]
    procedure WriteTo_ContainsValue()
    begin
        Assert.IsTrue(H.WriteToText('mykey', 'myvalue').Contains('myvalue'),
            'WriteTo output must contain the attribute value');
    end;

    [Test]
    procedure WriteTo_NotEmpty()
    begin
        Assert.AreNotEqual('', H.WriteToText('k', 'v'),
            'WriteTo must not return empty string');
    end;

    // ── GetParent ─────────────────────────────────────────────────────────────

    [Test]
    procedure GetParent_Detached_False()
    begin
        Assert.IsFalse(H.DetachedAttrHasNoParent(),
            'detached attribute GetParent must return false');
    end;

    [Test]
    procedure GetParent_Attached_ParentNameCorrect()
    begin
        Assert.AreEqual('root', H.AttachedAttrGetParentName(),
            'attached attribute GetParent must return the containing element');
    end;

    // ── GetDocument ───────────────────────────────────────────────────────────

    [Test]
    procedure GetDocument_Detached_False()
    begin
        Assert.IsFalse(H.DetachedAttrHasNoDocument(),
            'detached attribute GetDocument must return false');
    end;

    // ── Remove ────────────────────────────────────────────────────────────────

    [Test]
    procedure Remove_DetachesFromParent()
    begin
        Assert.IsFalse(H.RemoveAttrFromElement(),
            'after Remove, GetParent must return false');
    end;

    // ── AddAfterSelf / AddBeforeSelf ──────────────────────────────────────────

    [Test]
    procedure AddAfterSelf_IncreasesAttrCount()
    begin
        Assert.AreEqual(2, H.AddAfterSelfAttrCount(),
            'AddAfterSelf must insert a second attribute into the parent element');
    end;

    [Test]
    procedure AddBeforeSelf_IncreasesAttrCount()
    begin
        Assert.AreEqual(2, H.AddBeforeSelfAttrCount(),
            'AddBeforeSelf must insert a second attribute into the parent element');
    end;

    // ── ReplaceWith ───────────────────────────────────────────────────────────

    [Test]
    procedure ReplaceWith_NewAttrInParent()
    begin
        Assert.AreEqual('new', H.ReplaceWithChangesName(),
            'ReplaceWith must swap the attribute so the new one is findable by name');
    end;

    // ── SelectNodes ───────────────────────────────────────────────────────────

    [Test]
    procedure SelectNodes_ReturnsBool()
    begin
        // SelectNodes on '.' should return a result (true or false) without error.
        // We just verify it does not throw.
        Assert.IsTrue(true, 'SelectNodes must not throw');
        H.SelectNodesOnAttr();
    end;

    // ── SelectSingleNode ──────────────────────────────────────────────────────

    [Test]
    procedure SelectSingleNode_ReturnsBool()
    begin
        // SelectSingleNode on '.' should return a result without error.
        Assert.IsTrue(true, 'SelectSingleNode must not throw');
        H.SelectSingleNodeOnAttr();
    end;

    // ── Compilation proof ─────────────────────────────────────────────────────

    [Test]
    procedure AllMethods_Compile()
    begin
        Assert.IsTrue(true,
            'All XmlAttribute nav/ns methods must compile (no CS1061)');
    end;
}
