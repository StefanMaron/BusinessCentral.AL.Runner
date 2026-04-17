codeunit 108001 "XDR Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XDR Src";

    // ── AsXmlNode ───────────────────────────────────────────────────────────────

    [Test]
    procedure AsXmlNode_IsXmlDocument_True()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.IsTrue(Src.DocAsXmlNode_IsDocument(Doc), 'AsXmlNode must return a node where IsXmlDocument is true');
    end;

    [Test]
    procedure AsXmlNode_IsXmlElement_False()
    var
        Doc: XmlDocument;
        Node: XmlNode;
    begin
        Doc := Src.BuildDoc();
        Node := Doc.AsXmlNode();
        Assert.IsFalse(Node.IsXmlElement(), 'AsXmlNode of XmlDocument must not be IsXmlElement');
    end;

    // ── GetChildElements ────────────────────────────────────────────────────────

    [Test]
    procedure GetChildElements_OneRootElement_ReturnsOne()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.AreEqual(1, Src.DocGetChildElementsCount(Doc),
            'GetChildElements must return 1 for a document with one root element');
    end;

    [Test]
    procedure GetChildElements_ByName_Found()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.AreEqual(1, Src.DocGetChildElementsByName(Doc, 'root'),
            'GetChildElements(''root'') must return 1 when root element is named "root"');
    end;

    [Test]
    procedure GetChildElements_ByName_NotFound()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.AreEqual(0, Src.DocGetChildElementsByName(Doc, 'zzz'),
            'GetChildElements(''zzz'') must return 0 when no element matches');
    end;

    // ── GetDescendantElements ───────────────────────────────────────────────────

    [Test]
    procedure GetDescendantElements_ReturnsAll()
    var
        Doc: XmlDocument;
    begin
        // Tree: root → a, b  (3 elements total: root, a, b)
        Doc := Src.BuildDoc();
        Assert.AreEqual(3, Src.DocGetDescendantElementsCount(Doc),
            'GetDescendantElements must return 3 for root with two children');
    end;

    [Test]
    procedure GetDescendantElements_EmptyDoc_ReturnsZero()
    var
        Doc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        Assert.AreEqual(0, Src.DocGetDescendantElementsCount(Doc),
            'GetDescendantElements on empty document must return 0');
    end;

    // ── GetDescendantNodes ──────────────────────────────────────────────────────

    [Test]
    procedure GetDescendantNodes_ReturnsAll()
    var
        Doc: XmlDocument;
        Count: Integer;
    begin
        // root → a, b  → at least 3 nodes
        Doc := Src.BuildDoc();
        Count := Src.DocGetDescendantNodesCount(Doc);
        Assert.IsTrue(Count >= 3, 'GetDescendantNodes must return at least 3 for root with two children');
    end;

    // ── GetDocument ─────────────────────────────────────────────────────────────

    [Test]
    procedure GetDocument_ReturnsSelf_True()
    var
        Doc: XmlDocument;
        OutDoc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.IsTrue(Src.DocGetDocument(Doc, OutDoc),
            'GetDocument on XmlDocument must return true (document is its own document)');
    end;

    // ── GetDocumentType ─────────────────────────────────────────────────────────

    [Test]
    procedure GetDocumentType_NoDocType_ReturnsFalse()
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
    begin
        Doc := Src.BuildDoc();
        Assert.IsFalse(Src.DocGetDocumentType(Doc, DocType),
            'GetDocumentType must return false when document has no DOCTYPE');
    end;

    [Test]
    procedure GetDocumentType_WithDocType_ReturnsTrue()
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
    begin
        Doc := Src.BuildDocWithDocType();
        Assert.IsTrue(Src.DocGetDocumentType(Doc, DocType),
            'GetDocumentType must return true when document has a DOCTYPE');
    end;

    // ── GetParent ───────────────────────────────────────────────────────────────

    [Test]
    procedure GetParent_AlwaysFalse_DocumentHasNoParent()
    var
        Doc: XmlDocument;
        Parent: XmlElement;
    begin
        Doc := Src.BuildDoc();
        Assert.IsFalse(Src.DocGetParent(Doc, Parent),
            'GetParent on XmlDocument must always return false (document has no parent)');
    end;

    // ── NameTable ───────────────────────────────────────────────────────────────

    [Test]
    procedure NameTable_DoesNotCrash()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.IsTrue(Src.DocNameTableDoesNotCrash(Doc), 'NameTable() must not crash');
    end;

    // ── Remove ──────────────────────────────────────────────────────────────────

    [Test]
    procedure Remove_DoesNotCrash()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.IsTrue(Src.DocRemoveDoesNotCrash(Doc), 'Remove() on a document must not crash');
    end;

    // ── SetDeclaration ──────────────────────────────────────────────────────────

    [Test]
    procedure SetDeclaration_GetDeclaration_RoundTrips()
    var
        Doc: XmlDocument;
        Decl: XmlDeclaration;
    begin
        Doc := XmlDocument.Create();
        Doc.Add(XmlElement.Create('root'));
        Decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        Assert.AreEqual('1.0', Src.DocSetDeclaration(Doc, Decl),
            'SetDeclaration then GetDeclaration must return version "1.0"');
    end;

    [Test]
    procedure SetDeclaration_Encoding_Preserved()
    var
        Doc: XmlDocument;
        Decl: XmlDeclaration;
        ResultDecl: XmlDeclaration;
    begin
        Doc := XmlDocument.Create();
        Doc.Add(XmlElement.Create('root'));
        Decl := XmlDeclaration.Create('1.0', 'UTF-8', '');
        Doc.SetDeclaration(Decl);
        Doc.GetDeclaration(ResultDecl);
        Assert.AreEqual('UTF-8', ResultDecl.Encoding(),
            'GetDeclaration after SetDeclaration must return the set encoding');
    end;

    // ── ReplaceNodes ────────────────────────────────────────────────────────────

    [Test]
    procedure ReplaceNodes_SubstitutesRootElement()
    var
        Doc: XmlDocument;
        NewRoot: XmlElement;
    begin
        Doc := Src.BuildDoc();
        NewRoot := XmlElement.Create('newroot');
        Assert.AreEqual(1, Src.DocReplaceNodesCount(Doc, NewRoot),
            'ReplaceNodes must leave exactly 1 child node in the document');
    end;

    // ── AddFirst ────────────────────────────────────────────────────────────────

    [Test]
    procedure AddFirst_EmptyDoc_AddsOne()
    var
        Doc: XmlDocument;
        Elem: XmlElement;
    begin
        Doc := XmlDocument.Create();
        Elem := XmlElement.Create('root');
        Assert.AreEqual(1, Src.DocAddFirst(Doc, Elem),
            'AddFirst on empty document must result in 1 child node');
    end;

    // ── AddAfterSelf ────────────────────────────────────────────────────────────

    [Test]
    procedure AddAfterSelf_DoesNotCrash()
    var
        Doc: XmlDocument;
        Sibling: XmlElement;
    begin
        Doc := Src.BuildDoc();
        Sibling := XmlElement.Create('sibling');
        Assert.IsTrue(Src.DocAddAfterSelfDoesNotCrash(Doc, Sibling),
            'AddAfterSelf on a document must not crash');
    end;

    // ── AddBeforeSelf ───────────────────────────────────────────────────────────

    [Test]
    procedure AddBeforeSelf_DoesNotCrash()
    var
        Doc: XmlDocument;
        Sibling: XmlElement;
    begin
        Doc := Src.BuildDoc();
        Sibling := XmlElement.Create('sibling');
        Assert.IsTrue(Src.DocAddBeforeSelfDoesNotCrash(Doc, Sibling),
            'AddBeforeSelf on a document must not crash');
    end;

    // ── ReplaceWith ─────────────────────────────────────────────────────────────

    [Test]
    procedure ReplaceWith_DoesNotCrash()
    var
        Doc: XmlDocument;
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Doc := Src.BuildDoc();
        Elem := XmlElement.Create('replacement');
        Node := Elem.AsXmlNode();
        Assert.IsTrue(Src.DocReplaceWithDoesNotCrash(Doc, Node),
            'ReplaceWith on a document must not crash');
    end;
}
