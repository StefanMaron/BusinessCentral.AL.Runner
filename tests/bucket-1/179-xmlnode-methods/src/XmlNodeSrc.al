/// Helper codeunit exercising XmlNode type-checking, casting, and navigation methods.
codeunit 106000 "XNN Src"
{
    // ── Type checks ─────────────────────────────────────────────────────────────

    procedure IsElement(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlElement());
    end;

    procedure IsAttribute(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlAttribute());
    end;

    procedure IsText(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlText());
    end;

    procedure IsCData(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlCData());
    end;

    procedure IsComment(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlComment());
    end;

    procedure IsDeclaration(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlDeclaration());
    end;

    procedure IsDocument(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlDocument());
    end;

    procedure IsProcessingInstruction(Node: XmlNode): Boolean
    begin
        exit(Node.IsXmlProcessingInstruction());
    end;

    // ── Type casts ──────────────────────────────────────────────────────────────

    procedure AsElementName(Node: XmlNode): Text
    var
        Elem: XmlElement;
    begin
        Elem := Node.AsXmlElement();
        exit(Elem.Name);
    end;

    procedure AsAttributeName(Node: XmlNode): Text
    var
        Attr: XmlAttribute;
    begin
        Attr := Node.AsXmlAttribute();
        exit(Attr.Name);
    end;

    procedure AsTextValue(Node: XmlNode): Text
    var
        Txt: XmlText;
    begin
        Txt := Node.AsXmlText();
        exit(Txt.Value);
    end;

    procedure AsCDataValue(Node: XmlNode): Text
    var
        CData: XmlCData;
    begin
        CData := Node.AsXmlCData();
        exit(CData.Value);
    end;

    procedure AsCommentValue(Node: XmlNode): Text
    var
        Comment: XmlComment;
    begin
        Comment := Node.AsXmlComment();
        exit(Comment.Value);
    end;

    procedure AsDeclarationVersion(Node: XmlNode): Text
    var
        Decl: XmlDeclaration;
    begin
        Decl := Node.AsXmlDeclaration();
        exit(Decl.Version);
    end;

    procedure AsProcessingInstructionTarget(Node: XmlNode): Text
    var
        PI: XmlProcessingInstruction;
        Result: Text;
    begin
        PI := Node.AsXmlProcessingInstruction();
        PI.GetTarget(Result);
        exit(Result);
    end;

    procedure AsElementWrongType(Node: XmlNode): XmlElement
    var
        Elem: XmlElement;
    begin
        // Must throw an error when the node is not an element
        Elem := Node.AsXmlElement();
        exit(Elem);
    end;

    // ── WriteTo ─────────────────────────────────────────────────────────────────

    procedure WriteNodeToText(Node: XmlNode): Text
    var
        Result: Text;
    begin
        Node.WriteTo(Result);
        exit(Result);
    end;

    // ── GetParent ───────────────────────────────────────────────────────────────

    procedure NodeGetParent(Node: XmlNode; var Parent: XmlElement): Boolean
    begin
        exit(Node.GetParent(Parent));
    end;

    // ── GetDocument ─────────────────────────────────────────────────────────────

    procedure NodeGetDocument(Node: XmlNode; var Doc: XmlDocument): Boolean
    begin
        exit(Node.GetDocument(Doc));
    end;

    // ── SelectNodes ─────────────────────────────────────────────────────────────

    procedure NodeSelectNodesCount(Root: XmlElement; XPath: Text): Integer
    var
        Node: XmlNode;
        NodeList: XmlNodeList;
    begin
        Node := Root.AsXmlNode();
        Node.SelectNodes(XPath, NodeList);
        exit(NodeList.Count());
    end;

    // ── SelectSingleNode ────────────────────────────────────────────────────────

    procedure NodeSelectSingleNodeFound(Root: XmlElement; XPath: Text): Boolean
    var
        Node: XmlNode;
        Found: XmlNode;
    begin
        Node := Root.AsXmlNode();
        exit(Node.SelectSingleNode(XPath, Found));
    end;

    procedure NodeSelectSingleNodeName(Root: XmlElement; XPath: Text): Text
    var
        Node: XmlNode;
        Found: XmlNode;
    begin
        Node := Root.AsXmlNode();
        if Node.SelectSingleNode(XPath, Found) then
            exit(Found.AsXmlElement().Name);
        exit('');
    end;

    // ── Remove ──────────────────────────────────────────────────────────────────

    procedure RemoveNodeFromParent(Node: XmlNode)
    begin
        Node.Remove();
    end;

    // ── ReplaceWith ─────────────────────────────────────────────────────────────

    procedure ReplaceNodeWith(OldNode: XmlNode; NewNode: XmlNode)
    begin
        OldNode.ReplaceWith(NewNode);
    end;

    // ── AddAfterSelf ────────────────────────────────────────────────────────────

    procedure AddAfterSelfNode(Ref: XmlNode; NewNode: XmlNode)
    begin
        Ref.AddAfterSelf(NewNode);
    end;

    // ── AddBeforeSelf ───────────────────────────────────────────────────────────

    procedure AddBeforeSelfNode(Ref: XmlNode; NewNode: XmlNode)
    begin
        Ref.AddBeforeSelf(NewNode);
    end;

    // ── Helper: Build 2-child element tree ────────────────────────────────────
    procedure BuildTwoChildTree(): XmlElement
    var
        Root: XmlElement;
        Item1: XmlElement;
        Item2: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Item1 := XmlElement.Create('item');
        Item2 := XmlElement.Create('item');
        Root.Add(Item1);
        Root.Add(Item2);
        exit(Root);
    end;
}
