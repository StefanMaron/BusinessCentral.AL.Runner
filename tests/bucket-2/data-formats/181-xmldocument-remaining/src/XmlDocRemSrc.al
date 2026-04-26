/// Helper codeunit exercising remaining XmlDocument methods from issue #810.
codeunit 108000 "XDR Src"
{
    // ── AsXmlNode ───────────────────────────────────────────────────────────────

    procedure DocAsXmlNode_IsDocument(Doc: XmlDocument): Boolean
    var
        Node: XmlNode;
    begin
        Node := Doc.AsXmlNode();
        exit(Node.IsXmlDocument());
    end;

    // ── GetChildElements ────────────────────────────────────────────────────────

    procedure DocGetChildElementsCount(Doc: XmlDocument): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetChildElements();
        exit(Nodes.Count());
    end;

    procedure DocGetChildElementsByName(Doc: XmlDocument; ElemName: Text): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetChildElements(ElemName);
        exit(Nodes.Count());
    end;

    // ── GetDescendantElements ───────────────────────────────────────────────────

    procedure DocGetDescendantElementsCount(Doc: XmlDocument): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetDescendantElements();
        exit(Nodes.Count());
    end;

    // ── GetDescendantNodes ──────────────────────────────────────────────────────

    procedure DocGetDescendantNodesCount(Doc: XmlDocument): Integer
    var
        Nodes: XmlNodeList;
    begin
        Nodes := Doc.GetDescendantNodes();
        exit(Nodes.Count());
    end;

    // ── GetDocument ─────────────────────────────────────────────────────────────

    procedure DocGetDocument(Doc: XmlDocument; var OutDoc: XmlDocument): Boolean
    begin
        exit(Doc.GetDocument(OutDoc));
    end;

    // ── GetDocumentType ─────────────────────────────────────────────────────────

    procedure DocGetDocumentType(Doc: XmlDocument; var DocType: XmlDocumentType): Boolean
    begin
        exit(Doc.GetDocumentType(DocType));
    end;

    // ── GetParent ───────────────────────────────────────────────────────────────

    procedure DocGetParent(Doc: XmlDocument; var Parent: XmlElement): Boolean
    begin
        exit(Doc.GetParent(Parent));
    end;

    // ── NameTable ───────────────────────────────────────────────────────────────

    procedure DocNameTableDoesNotCrash(Doc: XmlDocument): Boolean
    var
        Tbl: XmlNameTable;
    begin
        Tbl := Doc.NameTable();
        exit(true);
    end;

    // ── SetDeclaration ──────────────────────────────────────────────────────────

    procedure DocSetDeclaration(var Doc: XmlDocument; Decl: XmlDeclaration): Text
    var
        ResultDecl: XmlDeclaration;
    begin
        Doc.SetDeclaration(Decl);
        if Doc.GetDeclaration(ResultDecl) then
            exit(ResultDecl.Version());
        exit('');
    end;

    // ── ReplaceNodes ────────────────────────────────────────────────────────────

    procedure DocReplaceNodesCount(var Doc: XmlDocument; NewRoot: XmlElement): Integer
    var
        Nodes: XmlNodeList;
    begin
        Doc.ReplaceNodes(NewRoot);
        Nodes := Doc.GetChildNodes();
        exit(Nodes.Count());
    end;

    // ── AddFirst ────────────────────────────────────────────────────────────────

    procedure DocAddFirst(var Doc: XmlDocument; Elem: XmlElement): Integer
    var
        Nodes: XmlNodeList;
    begin
        Doc.AddFirst(Elem);
        Nodes := Doc.GetChildNodes();
        exit(Nodes.Count());
    end;

    // ── Helper: build a programmatic document ──────────────────────────────────

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

    procedure BuildDocWithDocType(): XmlDocument
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
        Root: XmlElement;
    begin
        Doc := XmlDocument.Create();
        DocType := XmlDocumentType.Create('root');
        Doc.Add(DocType);
        Root := XmlElement.Create('root');
        Doc.Add(Root);
        exit(Doc);
    end;
}
