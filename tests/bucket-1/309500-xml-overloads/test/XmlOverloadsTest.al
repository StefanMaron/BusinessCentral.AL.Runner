/// Tests for XML overload methods — issue #1372.
/// Covers XmlDocument/XmlElement/XmlDocumentType missing Create, GetChildElements,
/// GetDescendantElements, ReadFrom (with XmlReadOptions), RemoveAttribute, SetAttribute overloads.
codeunit 309501 "XmlOverloads Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XmlOverloads Src";

    // ── XmlDocument.Create(Joker) ────────────────────────────────

    [Test]
    procedure DocCreate_FromNode_RootIsPresent()
    var
        Root: XmlElement;
        Doc: XmlDocument;
        OutRoot: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Doc := Src.DocCreateFromNode(Root.AsXmlNode());
        Assert.IsTrue(Doc.GetRoot(OutRoot), 'XmlDocument.Create(Joker) must create doc with root element');
        Assert.AreEqual('root', OutRoot.Name(), 'root element name must be "root"');
    end;

    // ── XmlDocument.GetChildElements(Text) ───────────────────────

    [Test]
    procedure DocGetChildElements_ByName_Found()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.AreEqual(1, Src.DocGetChildElementsByName(Doc, 'root'),
            'GetChildElements("root") must return 1 when root element is named "root"');
    end;

    [Test]
    procedure DocGetChildElements_ByName_NotFound()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.AreEqual(0, Src.DocGetChildElementsByName(Doc, 'zzz'),
            'GetChildElements("zzz") must return 0 when no element matches');
    end;

    // ── XmlDocument.GetChildElements(Text, Text) ─────────────────

    [Test]
    procedure DocGetChildElements_ByNameNs_Found()
    var
        Doc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        Doc.Add(XmlElement.Create('root', 'http://example.com/ns'));
        Assert.AreEqual(1, Src.DocGetChildElementsByNameNs(Doc, 'root', 'http://example.com/ns'),
            'GetChildElements(name, ns) must return 1 when element has matching namespace');
    end;

    [Test]
    procedure DocGetChildElements_ByNameNs_WrongNs()
    var
        Doc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        Doc.Add(XmlElement.Create('root', 'http://example.com/ns'));
        Assert.AreEqual(0, Src.DocGetChildElementsByNameNs(Doc, 'root', 'http://other.com/ns'),
            'GetChildElements(name, ns) must return 0 when namespace does not match');
    end;

    // ── XmlDocument.GetDescendantElements(Text) ──────────────────

    [Test]
    procedure DocGetDescendantElements_ByName_Found()
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Child: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Child := XmlElement.Create('leaf');
        Root.Add(Child);
        Doc := XmlDocument.Create();
        Doc.Add(Root);
        Assert.AreEqual(1, Src.DocGetDescendantElementsByName(Doc, 'leaf'),
            'GetDescendantElements("leaf") must return 1 for a nested element');
    end;

    [Test]
    procedure DocGetDescendantElements_ByName_NotFound()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDoc();
        Assert.AreEqual(0, Src.DocGetDescendantElementsByName(Doc, 'zzz'),
            'GetDescendantElements("zzz") must return 0 when no element matches');
    end;

    // ── XmlDocument.GetDescendantElements(Text, Text) ────────────

    [Test]
    procedure DocGetDescendantElements_ByNameNs_Found()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDocWithNsChild();
        Assert.AreEqual(1, Src.DocGetDescendantElementsByNameNs(Doc, 'child', 'http://example.com/ns'),
            'GetDescendantElements(name, ns) must return 1 for matching descendant');
    end;

    [Test]
    procedure DocGetDescendantElements_ByNameNs_WrongNs()
    var
        Doc: XmlDocument;
    begin
        Doc := Src.BuildDocWithNsChild();
        Assert.AreEqual(0, Src.DocGetDescendantElementsByNameNs(Doc, 'child', 'http://other.com/ns'),
            'GetDescendantElements(name, ns) must return 0 when namespace does not match');
    end;

    // ── XmlDocument.ReadFrom(Text, var XmlDocument) ──────────────

    [Test]
    procedure DocReadFromText_ParsesXml()
    var
        Doc: XmlDocument;
        Root: XmlElement;
    begin
        Src.DocReadFromText('<root><child/></root>', Doc);
        Assert.IsTrue(Doc.GetRoot(Root), 'ReadFrom(Text) must parse XML and set root');
        Assert.AreEqual('root', Root.Name(), 'parsed root element name must be "root"');
    end;

    [Test]
    procedure DocReadFromText_ChildCount()
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Children: XmlNodeList;
    begin
        Src.DocReadFromText('<root><a/><b/></root>', Doc);
        Doc.GetRoot(Root);
        Children := Root.GetChildElements();
        Assert.AreEqual(2, Children.Count(), 'parsed root must have 2 child elements');
    end;

    // ── XmlDocument.ReadFrom(Text, XmlReadOptions, var XmlDocument) ─

    [Test]
    procedure DocReadFromTextWithOptions_ParsesXml()
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Opts: XmlReadOptions;
    begin
        Src.DocReadFromTextWithOptions('<root/>', Opts, Doc);
        Assert.IsTrue(Doc.GetRoot(Root), 'ReadFrom(Text, Options) must parse XML and set root');
        Assert.AreEqual('root', Root.Name(), 'parsed root element name must be "root"');
    end;

    [Test]
    procedure DocReadFromTextWithOptions_PreserveWhitespaceFalse_SameResult()
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Opts: XmlReadOptions;
    begin
        // Default XmlReadOptions (PreserveWhitespace=false) should still parse correctly
        Src.DocReadFromTextWithOptions('<root><child/></root>', Opts, Doc);
        Doc.GetRoot(Root);
        Assert.AreEqual('root', Root.Name(), 'ReadFrom(Text, Options) with default options must parse root correctly');
    end;

    // ── XmlDocumentType.Create(Text, Text) ───────────────────────

    [Test]
    procedure DocTypeCreate2_NameAndPublicId()
    var
        DocType: XmlDocumentType;
        Name: Text;
        PublicId: Text;
    begin
        DocType := Src.DocTypeCreate2('html', '-//W3C//DTD');
        DocType.GetName(Name);
        DocType.GetPublicId(PublicId);
        Assert.AreEqual('html', Name, 'XmlDocumentType.Create(2) name must be "html"');
        Assert.AreEqual('-//W3C//DTD', PublicId, 'XmlDocumentType.Create(2) publicId must be set');
    end;

    [Test]
    procedure DocTypeCreate2_CanAddToDocument()
    var
        DocType: XmlDocumentType;
        Doc: XmlDocument;
        OutDocType: XmlDocumentType;
    begin
        DocType := Src.DocTypeCreate2('html', '-//W3C//DTD');
        Doc := XmlDocument.Create();
        Doc.Add(DocType);
        Doc.Add(XmlElement.Create('html'));
        Assert.IsTrue(Doc.GetDocumentType(OutDocType),
            'XmlDocumentType.Create(2) result must be retrievable from the document');
    end;

    // ── XmlDocumentType.Create(Text, Text, Text) ─────────────────

    [Test]
    procedure DocTypeCreate3_AllValues()
    var
        DocType: XmlDocumentType;
        Name: Text;
        PublicId: Text;
        SystemId: Text;
    begin
        DocType := Src.DocTypeCreate3('html', '-//W3C//DTD', 'http://www.w3.org/TR/html4/strict.dtd');
        DocType.GetName(Name);
        DocType.GetPublicId(PublicId);
        DocType.GetSystemId(SystemId);
        Assert.AreEqual('html', Name, 'XmlDocumentType.Create(3) name must be "html"');
        Assert.AreEqual('-//W3C//DTD', PublicId, 'XmlDocumentType.Create(3) publicId must be set');
        Assert.AreEqual('http://www.w3.org/TR/html4/strict.dtd', SystemId,
            'XmlDocumentType.Create(3) systemId must be set');
    end;

    // ── XmlDocumentType.Create(Text, Text, Text, Text) ───────────

    [Test]
    procedure DocTypeCreate4_AllValues()
    var
        DocType: XmlDocumentType;
        Name: Text;
        InternalSubset: Text;
    begin
        DocType := Src.DocTypeCreate4('html', '-//W3C//DTD',
            'http://www.w3.org/TR/html4/strict.dtd', '<!ELEMENT internal EMPTY>');
        DocType.GetName(Name);
        DocType.GetInternalSubset(InternalSubset);
        Assert.AreEqual('html', Name, 'XmlDocumentType.Create(4) name must be "html"');
        Assert.AreEqual('<!ELEMENT internal EMPTY>', InternalSubset,
            'XmlDocumentType.Create(4) internalSubset must be set');
    end;

    // ── XmlElement.Create(Text, Text) ────────────────────────────

    [Test]
    procedure ElemCreate2_NamespaceUri()
    var
        Elem: XmlElement;
    begin
        Elem := Src.ElemCreate2('root', 'http://example.com/ns');
        Assert.AreEqual('http://example.com/ns', Elem.NamespaceUri(),
            'XmlElement.Create(2) NamespaceUri must match the given namespace');
    end;

    [Test]
    procedure ElemCreate2_LocalName()
    var
        Elem: XmlElement;
    begin
        Elem := Src.ElemCreate2('root', 'http://example.com/ns');
        Assert.AreEqual('root', Elem.LocalName(),
            'XmlElement.Create(2) LocalName must be "root"');
    end;

    // ── XmlElement.Create(Text, Joker) ───────────────────────────

    [Test]
    procedure ElemCreate2Node_HasChild()
    var
        Elem: XmlElement;
        Child: XmlElement;
        Nodes: XmlNodeList;
    begin
        Child := XmlElement.Create('child');
        Elem := Src.ElemCreate2Node('root', Child.AsXmlNode());
        Nodes := Elem.GetChildElements();
        Assert.AreEqual(1, Nodes.Count(),
            'XmlElement.Create(name, Joker) must include the given node as child');
    end;

    [Test]
    procedure ElemCreate2Node_ChildName()
    var
        Elem: XmlElement;
        Child: XmlElement;
        ChildNode: XmlNode;
        Nodes: XmlNodeList;
        OutChild: XmlElement;
    begin
        Child := XmlElement.Create('child');
        Elem := Src.ElemCreate2Node('root', Child.AsXmlNode());
        Nodes := Elem.GetChildElements();
        Nodes.Get(1, ChildNode);
        OutChild := ChildNode.AsXmlElement();
        Assert.AreEqual('child', OutChild.Name(),
            'XmlElement.Create(name, Joker) child element name must be "child"');
    end;

    // ── XmlElement.Create(Text, Text, Joker) ─────────────────────

    [Test]
    procedure ElemCreate3Node_HasChild()
    var
        Elem: XmlElement;
        Child: XmlElement;
        Nodes: XmlNodeList;
    begin
        Child := XmlElement.Create('child');
        Elem := Src.ElemCreate3Node('root', 'http://example.com/ns', Child.AsXmlNode());
        Nodes := Elem.GetChildElements();
        Assert.AreEqual(1, Nodes.Count(),
            'XmlElement.Create(name, ns, Joker) must include the given node as child');
    end;

    [Test]
    procedure ElemCreate3Node_NamespaceUri()
    var
        Elem: XmlElement;
        Child: XmlElement;
    begin
        Child := XmlElement.Create('child');
        Elem := Src.ElemCreate3Node('root', 'http://example.com/ns', Child.AsXmlNode());
        Assert.AreEqual('http://example.com/ns', Elem.NamespaceUri(),
            'XmlElement.Create(name, ns, Joker) NamespaceUri must be set');
    end;

    // ── XmlElement.GetChildElements(Text) ────────────────────────

    [Test]
    procedure ElemGetChildElements_ByName_MultipleMatches()
    var
        Root: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Root.Add(XmlElement.Create('a'));
        Root.Add(XmlElement.Create('b'));
        Root.Add(XmlElement.Create('a'));
        Assert.AreEqual(2, Src.ElemGetChildElementsByName(Root, 'a'),
            'GetChildElements("a") must return 2 when two children have name "a"');
    end;

    [Test]
    procedure ElemGetChildElements_ByName_NotFound()
    var
        Root: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Root.Add(XmlElement.Create('a'));
        Assert.AreEqual(0, Src.ElemGetChildElementsByName(Root, 'zzz'),
            'GetChildElements("zzz") must return 0 when no children match');
    end;

    // ── XmlElement.GetChildElements(Text, Text) ──────────────────

    [Test]
    procedure ElemGetChildElements_ByNameNs_Found()
    var
        Root: XmlElement;
        NsChild: XmlElement;
    begin
        Root := XmlElement.Create('root');
        NsChild := XmlElement.Create('child', 'http://example.com/ns');
        Root.Add(NsChild);
        Root.Add(XmlElement.Create('other'));
        Assert.AreEqual(1, Src.ElemGetChildElementsByNameNs(Root, 'child', 'http://example.com/ns'),
            'GetChildElements(name, ns) must return 1 for matching namespace child');
    end;

    [Test]
    procedure ElemGetChildElements_ByNameNs_WrongNs()
    var
        Root: XmlElement;
        NsChild: XmlElement;
    begin
        Root := XmlElement.Create('root');
        NsChild := XmlElement.Create('child', 'http://example.com/ns');
        Root.Add(NsChild);
        Assert.AreEqual(0, Src.ElemGetChildElementsByNameNs(Root, 'child', 'http://other.com/ns'),
            'GetChildElements(name, ns) must return 0 when namespace does not match');
    end;

    // ── XmlElement.GetDescendantElements(Text) ───────────────────

    [Test]
    procedure ElemGetDescendantElements_ByName_Found()
    var
        Root: XmlElement;
        Child: XmlElement;
        GrandChild: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Child := XmlElement.Create('child');
        GrandChild := XmlElement.Create('leaf');
        Child.Add(GrandChild);
        Root.Add(Child);
        Assert.AreEqual(1, Src.ElemGetDescendantElementsByName(Root, 'leaf'),
            'GetDescendantElements("leaf") must return 1 for a nested element');
    end;

    [Test]
    procedure ElemGetDescendantElements_ByName_NotFound()
    var
        Root: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Root.Add(XmlElement.Create('child'));
        Assert.AreEqual(0, Src.ElemGetDescendantElementsByName(Root, 'zzz'),
            'GetDescendantElements("zzz") must return 0 when no element matches');
    end;

    // ── XmlElement.GetDescendantElements(Text, Text) ─────────────

    [Test]
    procedure ElemGetDescendantElements_ByNameNs_Found()
    var
        Root: XmlElement;
        Child: XmlElement;
        NsGrandChild: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Child := XmlElement.Create('child');
        NsGrandChild := XmlElement.Create('leaf', 'http://example.com/ns');
        Child.Add(NsGrandChild);
        Root.Add(Child);
        Assert.AreEqual(1, Src.ElemGetDescendantElementsByNameNs(Root, 'leaf', 'http://example.com/ns'),
            'GetDescendantElements(name, ns) must return 1 for matching descendant');
    end;

    [Test]
    procedure ElemGetDescendantElements_ByNameNs_WrongNs()
    var
        Root: XmlElement;
        Child: XmlElement;
        NsGrandChild: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Child := XmlElement.Create('child');
        NsGrandChild := XmlElement.Create('leaf', 'http://example.com/ns');
        Child.Add(NsGrandChild);
        Root.Add(Child);
        Assert.AreEqual(0, Src.ElemGetDescendantElementsByNameNs(Root, 'leaf', 'http://other.com/ns'),
            'GetDescendantElements(name, ns) must return 0 when namespace does not match');
    end;

    // ── XmlElement.RemoveAttribute(Text, Text) ───────────────────

    [Test]
    procedure ElemRemoveAttribute_ByNameNs_RemovesIt()
    var
        Elem: XmlElement;
    begin
        Elem := XmlElement.Create('root');
        Elem.SetAttribute('id', 'http://example.com/ns', 'val1');
        Assert.IsTrue(Elem.HasAttributes(), 'element must have attribute before RemoveAttribute(name, ns)');
        Src.ElemRemoveAttributeByNameNs(Elem, 'id', 'http://example.com/ns');
        Assert.IsFalse(Elem.HasAttributes(),
            'element must have no attributes after RemoveAttribute(name, ns)');
    end;

    [Test]
    procedure ElemRemoveAttribute_ByNameNs_WrongNsDoesNotRemove()
    var
        Elem: XmlElement;
    begin
        Elem := XmlElement.Create('root');
        Elem.SetAttribute('id', 'http://example.com/ns', 'val1');
        Src.ElemRemoveAttributeByNameNs(Elem, 'id', 'http://other.com/ns');
        // Wrong namespace — the original attribute should still be there
        Assert.IsTrue(Elem.HasAttributes(),
            'RemoveAttribute(name, wrong-ns) must not remove an attribute with a different namespace');
    end;

    // ── XmlElement.RemoveAttribute(XmlAttribute) ─────────────────

    [Test]
    procedure ElemRemoveAttribute_ByObj_RemovesIt()
    var
        Elem: XmlElement;
        Attr: XmlAttribute;
        Attrs: XmlAttributeCollection;
    begin
        Elem := XmlElement.Create('root');
        Elem.SetAttribute('id', 'val1');
        Assert.IsTrue(Elem.HasAttributes(), 'element must have attribute before RemoveAttribute(XmlAttribute)');
        Attrs := Elem.Attributes();
        Attrs.Get('id', Attr);
        Src.ElemRemoveAttributeByObj(Elem, Attr);
        Assert.IsFalse(Elem.HasAttributes(),
            'element must have no attributes after RemoveAttribute(XmlAttribute)');
    end;

    [Test]
    procedure ElemRemoveAttribute_ByObj_ValueWasCorrect()
    var
        Elem: XmlElement;
        Attr: XmlAttribute;
        Attrs: XmlAttributeCollection;
    begin
        // Verify that we removed the right attribute (id = 'val1')
        Elem := XmlElement.Create('root');
        Elem.SetAttribute('id', 'val1');
        Attrs := Elem.Attributes();
        Attrs.Get('id', Attr);
        Assert.AreEqual('val1', Attr.Value(), 'the retrieved attribute value must be "val1" before removal');
        Src.ElemRemoveAttributeByObj(Elem, Attr);
        Assert.IsFalse(Elem.HasAttributes(), 'attribute must be gone after RemoveAttribute(XmlAttribute)');
    end;

    // ── XmlElement.SetAttribute(Text, Text, Text) ────────────────

    [Test]
    procedure ElemSetAttribute3_HasAttributes()
    var
        Elem: XmlElement;
    begin
        Elem := XmlElement.Create('root');
        Src.ElemSetAttribute3(Elem, 'id', 'http://example.com/ns', 'myvalue');
        Assert.IsTrue(Elem.HasAttributes(),
            'SetAttribute(name, ns, value) must add an attribute to the element');
    end;

    [Test]
    procedure ElemSetAttribute3_ThenRemove()
    var
        Elem: XmlElement;
    begin
        // SetAttribute then RemoveAttribute round-trip
        Elem := XmlElement.Create('root');
        Src.ElemSetAttribute3(Elem, 'id', 'http://example.com/ns', 'myvalue');
        Assert.IsTrue(Elem.HasAttributes(), 'SetAttribute(3) must add attribute');
        Src.ElemRemoveAttributeByNameNs(Elem, 'id', 'http://example.com/ns');
        Assert.IsFalse(Elem.HasAttributes(),
            'RemoveAttribute(name, ns) must remove the attribute set by SetAttribute(3)');
    end;
}
