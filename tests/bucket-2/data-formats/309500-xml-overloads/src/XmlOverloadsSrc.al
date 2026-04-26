/// Helper codeunit for XML overload tests — issue #1372.
/// Tests XmlDocument/XmlElement/XmlDocumentType missing overloads:
/// Create, GetChildElements, GetDescendantElements, ReadFrom (with XmlReadOptions),
/// RemoveAttribute, SetAttribute.
codeunit 309500 "XmlOverloads Src"
{
    // ── XmlDocument.Create(Joker) ────────────────────────────────

    procedure DocCreateFromNode(Node: XmlNode): XmlDocument
    begin
        exit(XmlDocument.Create(Node));
    end;

    // ── XmlDocument.GetChildElements(Text) ───────────────────────

    procedure DocGetChildElementsByName(Doc: XmlDocument; ElemName: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetChildElements(ElemName);
        exit(Nodes.Count());
    end;

    // ── XmlDocument.GetChildElements(Text, Text) ─────────────────

    procedure DocGetChildElementsByNameNs(Doc: XmlDocument; ElemName: Text; Ns: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetChildElements(ElemName, Ns);
        exit(Nodes.Count());
    end;

    // ── XmlDocument.GetDescendantElements(Text) ──────────────────

    procedure DocGetDescendantElementsByName(Doc: XmlDocument; ElemName: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetDescendantElements(ElemName);
        exit(Nodes.Count());
    end;

    // ── XmlDocument.GetDescendantElements(Text, Text) ────────────

    procedure DocGetDescendantElementsByNameNs(Doc: XmlDocument; ElemName: Text; Ns: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetDescendantElements(ElemName, Ns);
        exit(Nodes.Count());
    end;

    // ── XmlDocument.ReadFrom(Text, var XmlDocument) ──────────────

    procedure DocReadFromText(XmlText: Text; var Doc: XmlDocument): Boolean
    begin
        exit(XmlDocument.ReadFrom(XmlText, Doc));
    end;

    // ── XmlDocument.ReadFrom(Text, XmlReadOptions, var XmlDocument) ─

    procedure DocReadFromTextWithOptions(XmlText: Text; Opts: XmlReadOptions; var Doc: XmlDocument): Boolean
    begin
        exit(XmlDocument.ReadFrom(XmlText, Opts, Doc));
    end;

    // ── XmlDocumentType.Create(Text, Text) ───────────────────────

    procedure DocTypeCreate2(Name: Text; PublicId: Text): XmlDocumentType
    begin
        exit(XmlDocumentType.Create(Name, PublicId));
    end;

    // ── XmlDocumentType.Create(Text, Text, Text) ─────────────────

    procedure DocTypeCreate3(Name: Text; PublicId: Text; SystemId: Text): XmlDocumentType
    begin
        exit(XmlDocumentType.Create(Name, PublicId, SystemId));
    end;

    // ── XmlDocumentType.Create(Text, Text, Text, Text) ───────────

    procedure DocTypeCreate4(Name: Text; PublicId: Text; SystemId: Text; InternalSubset: Text): XmlDocumentType
    begin
        exit(XmlDocumentType.Create(Name, PublicId, SystemId, InternalSubset));
    end;

    // ── XmlElement.Create(Text, Text) ────────────────────────────

    procedure ElemCreate2(Name: Text; Ns: Text): XmlElement
    begin
        exit(XmlElement.Create(Name, Ns));
    end;

    // ── XmlElement.Create(Text, Joker) ───────────────────────────

    procedure ElemCreate2Node(Name: Text; Node: XmlNode): XmlElement
    begin
        exit(XmlElement.Create(Name, Node));
    end;

    // ── XmlElement.Create(Text, Text, Joker) ─────────────────────

    procedure ElemCreate3Node(Name: Text; Ns: Text; Node: XmlNode): XmlElement
    begin
        exit(XmlElement.Create(Name, Ns, Node));
    end;

    // ── XmlElement.GetChildElements(Text) ────────────────────────

    procedure ElemGetChildElementsByName(Elem: XmlElement; ElemName: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Elem.GetChildElements(ElemName);
        exit(Nodes.Count());
    end;

    // ── XmlElement.GetChildElements(Text, Text) ──────────────────

    procedure ElemGetChildElementsByNameNs(Elem: XmlElement; ElemName: Text; Ns: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Elem.GetChildElements(ElemName, Ns);
        exit(Nodes.Count());
    end;

    // ── XmlElement.GetDescendantElements(Text) ───────────────────

    procedure ElemGetDescendantElementsByName(Elem: XmlElement; ElemName: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Elem.GetDescendantElements(ElemName);
        exit(Nodes.Count());
    end;

    // ── XmlElement.GetDescendantElements(Text, Text) ─────────────

    procedure ElemGetDescendantElementsByNameNs(Elem: XmlElement; ElemName: Text; Ns: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Elem.GetDescendantElements(ElemName, Ns);
        exit(Nodes.Count());
    end;

    // ── XmlElement.RemoveAttribute(Text, Text) ───────────────────

    procedure ElemRemoveAttributeByNameNs(var Elem: XmlElement; AttrName: Text; Ns: Text)
    begin
        Elem.RemoveAttribute(AttrName, Ns);
    end;

    // ── XmlElement.RemoveAttribute(XmlAttribute) ─────────────────

    procedure ElemRemoveAttributeByObj(var Elem: XmlElement; Attr: XmlAttribute)
    begin
        Elem.RemoveAttribute(Attr);
    end;

    // ── XmlElement.SetAttribute(Text, Text, Text) ────────────────

    procedure ElemSetAttribute3(var Elem: XmlElement; AttrName: Text; Ns: Text; AttrValue: Text)
    begin
        Elem.SetAttribute(AttrName, Ns, AttrValue);
    end;

    // ── Helpers ──────────────────────────────────────────────────

    procedure BuildDoc(): XmlDocument
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Child1: XmlElement;
        Child2: XmlElement;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create('root');
        Child1 := XmlElement.Create('a');
        Child2 := XmlElement.Create('b');
        Root.Add(Child1);
        Root.Add(Child2);
        Doc.Add(Root);
        exit(Doc);
    end;

    procedure BuildDocWithNsChild(): XmlDocument
    var
        Doc: XmlDocument;
        Root: XmlElement;
        NsChild: XmlElement;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create('root');
        NsChild := XmlElement.Create('child', 'http://example.com/ns');
        Root.Add(NsChild);
        Doc.Add(Root);
        exit(Doc);
    end;
}
