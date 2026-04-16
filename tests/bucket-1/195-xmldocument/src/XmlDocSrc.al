/// Source codeunit exercising XmlDocument creation, parsing, navigation, and serialization.
codeunit 100200 "XmlDoc Src"
{
    // ── Create ────────────────────────────────────────────────────────────────

    /// Create an empty XmlDocument — must not throw.
    procedure CreateDoc(): XmlDocument
    begin
        exit(XmlDocument.Create());
    end;

    // ── Add + GetRoot ─────────────────────────────────────────────────────────

    /// Add an XmlElement as the root and retrieve it back via GetRoot.
    procedure AddAndGetRoot(elemName: Text): Text
    var
        Doc: XmlDocument;
        Root: XmlElement;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create(elemName);
        Doc.Add(Root);
        Doc.GetRoot(Root);
        exit(Root.Name());
    end;

    // ── ReadFrom ──────────────────────────────────────────────────────────────

    /// Parse well-formed XML text and return the root element name.
    procedure ReadFromAndGetRootName(XmlText: Text): Text
    var
        Doc: XmlDocument;
        Root: XmlElement;
    begin
        XmlDocument.ReadFrom(XmlText, Doc);
        Doc.GetRoot(Root);
        exit(Root.Name());
    end;

    /// Parse invalid XML — must raise an error.
    procedure ReadFromInvalid(BadXml: Text)
    var
        Doc: XmlDocument;
    begin
        XmlDocument.ReadFrom(BadXml, Doc);
    end;

    // ── WriteTo ───────────────────────────────────────────────────────────────

    /// Serialize a document to text; the result must contain the root element name.
    procedure WriteToText(elemName: Text): Text
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Result: Text;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create(elemName);
        Doc.Add(Root);
        Doc.WriteTo(Result);
        exit(Result);
    end;

    // ── GetChildNodes ─────────────────────────────────────────────────────────

    /// Return the count of direct child nodes of the document.
    procedure GetChildNodesCount(elemName: Text): Integer
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Nodes: XmlNodeList;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create(elemName);
        Doc.Add(Root);
        Nodes := Doc.GetChildNodes();
        exit(Nodes.Count());
    end;

    // ── SelectNodes ───────────────────────────────────────────────────────────

    /// Use XPath to select descendant elements; return count of matches.
    procedure SelectNodesCount(XmlText: Text; XPath: Text): Integer
    var
        Doc: XmlDocument;
        NodeList: XmlNodeList;
    begin
        XmlDocument.ReadFrom(XmlText, Doc);
        Doc.SelectNodes(XPath, NodeList);
        exit(NodeList.Count());
    end;

    // ── GetDeclaration ────────────────────────────────────────────────────────

    /// Parse XML with a declaration; GetDeclaration must return true and the version.
    procedure GetDeclarationVersion(XmlText: Text): Text
    var
        Doc: XmlDocument;
        Decl: XmlDeclaration;
    begin
        XmlDocument.ReadFrom(XmlText, Doc);
        if Doc.GetDeclaration(Decl) then
            exit(Decl.Version());
        exit('');
    end;

    // ── RemoveNodes ───────────────────────────────────────────────────────────

    /// Add two children, call RemoveNodes, and return count of remaining children.
    procedure RemoveNodesCount(): Integer
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Child1: XmlElement;
        Child2: XmlElement;
        Nodes: XmlNodeList;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create('root');
        Child1 := XmlElement.Create('a');
        Child2 := XmlElement.Create('b');
        Root.Add(Child1);
        Root.Add(Child2);
        Doc.Add(Root);
        Root.RemoveNodes();
        Root.GetChildNodes(Nodes);
        exit(Nodes.Count());
    end;
}
