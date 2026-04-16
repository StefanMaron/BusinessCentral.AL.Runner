/// Helper codeunit exercising XmlDocumentType — issue #769.
codeunit 101000 "XDT Src"
{
    procedure CreateDocType(): XmlDocumentType
    begin
        exit(XmlDocumentType.Create('root', '', '', ''));
    end;

    procedure WriteToText(DocType: XmlDocumentType): Text
    var
        Result: Text;
    begin
        DocType.WriteTo(Result);
        exit(Result);
    end;

    procedure AsXmlNodeIsDocType(DocType: XmlDocumentType): Boolean
    var
        Node: XmlNode;
    begin
        Node := DocType.AsXmlNode();
        exit(Node.IsXmlDocumentType());
    end;

    procedure GetDocumentReturnsTrue(DocType: XmlDocumentType): Boolean
    var
        Doc: XmlDocument;
    begin
        // A standalone DocType has no parent document → returns false
        exit(DocType.GetDocument(Doc));
    end;

    procedure GetParentElementReturnsFalse(DocType: XmlDocumentType): Boolean
    var
        Parent: XmlElement;
    begin
        exit(DocType.GetParent(Parent));
    end;

    procedure SelectNodesCount(): Integer
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
        Root: XmlElement;
        Nodes: XmlNodeList;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Root := XmlElement.Create('root');
        Doc := XmlDocument.Create();
        Doc.Add(DocType);
        Doc.Add(Root);
        if DocType.SelectNodes('*', Nodes) then
            exit(Nodes.Count())
        else
            exit(0);
    end;

    procedure SelectSingleNodeFound(DocType: XmlDocumentType): Boolean
    var
        Node: XmlNode;
    begin
        exit(DocType.SelectSingleNode('//x', Node));
    end;

    procedure RemoveFromDocument(var Doc: XmlDocument): Boolean
    var
        DocType: XmlDocumentType;
    begin
        Doc.GetDocumentType(DocType);
        DocType.Remove();
        // After Remove, GetDocumentType should return false
        exit(Doc.GetDocumentType(DocType));
    end;

    /// AddAfterSelf inserts a PI sibling after the doctype; child count increases.
    procedure AddAfterSelf_ChildCountIncreases(): Integer
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
        Pi: XmlProcessingInstruction;
        Root: XmlElement;
        Nodes: XmlNodeList;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Root := XmlElement.Create('root');
        Doc := XmlDocument.Create();
        Doc.Add(DocType);
        Doc.Add(Root);
        Pi := XmlProcessingInstruction.Create('pi', 'data');
        DocType.AddAfterSelf(Pi.AsXmlNode());
        Nodes := Doc.GetChildNodes();
        exit(Nodes.Count());
    end;

    /// ReplaceWith swaps the DocType for a PI; document no longer has doctype.
    procedure ReplaceWith_DocTypeGone(): Boolean
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
        Pi: XmlProcessingInstruction;
        DocType2: XmlDocumentType;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Doc := XmlDocument.Create();
        Doc.Add(DocType);
        Pi := XmlProcessingInstruction.Create('pi', 'data');
        DocType.ReplaceWith(Pi.AsXmlNode());
        // GetDocumentType should now return false (no doctype in document)
        exit(Doc.GetDocumentType(DocType2));
    end;

    /// AddBeforeSelf inserts a PI sibling before the doctype; child count increases.
    procedure AddBeforeSelf_ChildCountIncreases(): Integer
    var
        Doc: XmlDocument;
        DocType: XmlDocumentType;
        Pi: XmlProcessingInstruction;
        Root: XmlElement;
        Nodes: XmlNodeList;
    begin
        DocType := XmlDocumentType.Create('root', '', '', '');
        Root := XmlElement.Create('root');
        Doc := XmlDocument.Create();
        Doc.Add(DocType);
        Doc.Add(Root);
        Pi := XmlProcessingInstruction.Create('pi', 'data');
        DocType.AddBeforeSelf(Pi.AsXmlNode());
        Nodes := Doc.GetChildNodes();
        exit(Nodes.Count());
    end;
}
