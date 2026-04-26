codeunit 106001 "XNN Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "XNN Src";

    // ── IsXmlElement ────────────────────────────────────────────────────────────

    [Test]
    procedure IsElement_True_ForElement()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        Assert.IsTrue(Src.IsElement(Node), 'IsXmlElement must be true for an element node');
    end;

    [Test]
    procedure IsElement_False_ForAttribute()
    var
        Attr: XmlAttribute;
        Node: XmlNode;
    begin
        Attr := XmlAttribute.Create('id', 'val');
        Node := Attr.AsXmlNode();
        Assert.IsFalse(Src.IsElement(Node), 'IsXmlElement must be false for an attribute node');
    end;

    // ── IsXmlAttribute ──────────────────────────────────────────────────────────

    [Test]
    procedure IsAttribute_True_ForAttribute()
    var
        Attr: XmlAttribute;
        Node: XmlNode;
    begin
        Attr := XmlAttribute.Create('myattr', 'myval');
        Node := Attr.AsXmlNode();
        Assert.IsTrue(Src.IsAttribute(Node), 'IsXmlAttribute must be true for an attribute node');
    end;

    [Test]
    procedure IsAttribute_False_ForElement()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.IsAttribute(Node), 'IsXmlAttribute must be false for an element node');
    end;

    // ── IsXmlText ───────────────────────────────────────────────────────────────

    [Test]
    procedure IsText_True_ForText()
    var
        Txt: XmlText;
        Node: XmlNode;
    begin
        Txt := XmlText.Create('hello');
        Node := Txt.AsXmlNode();
        Assert.IsTrue(Src.IsText(Node), 'IsXmlText must be true for a text node');
    end;

    [Test]
    procedure IsText_False_ForElement()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.IsText(Node), 'IsXmlText must be false for an element node');
    end;

    // ── IsXmlCData ──────────────────────────────────────────────────────────────

    [Test]
    procedure IsCData_True_ForCData()
    var
        CData: XmlCData;
        Node: XmlNode;
    begin
        CData := XmlCData.Create('rawdata');
        Node := CData.AsXmlNode();
        Assert.IsTrue(Src.IsCData(Node), 'IsXmlCData must be true for a CDATA node');
    end;

    [Test]
    procedure IsCData_False_ForText()
    var
        Txt: XmlText;
        Node: XmlNode;
    begin
        Txt := XmlText.Create('hello');
        Node := Txt.AsXmlNode();
        Assert.IsFalse(Src.IsCData(Node), 'IsXmlCData must be false for a text node');
    end;

    // ── IsXmlComment ────────────────────────────────────────────────────────────

    [Test]
    procedure IsComment_True_ForComment()
    var
        Comment: XmlComment;
        Node: XmlNode;
    begin
        Comment := XmlComment.Create('a comment');
        Node := Comment.AsXmlNode();
        Assert.IsTrue(Src.IsComment(Node), 'IsXmlComment must be true for a comment node');
    end;

    [Test]
    procedure IsComment_False_ForElement()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.IsComment(Node), 'IsXmlComment must be false for an element node');
    end;

    // ── IsXmlDeclaration ────────────────────────────────────────────────────────

    [Test]
    procedure IsDeclaration_True_ForDeclaration()
    var
        Decl: XmlDeclaration;
        Node: XmlNode;
    begin
        Decl := XmlDeclaration.Create('1.0', 'utf-8', '');
        Node := Decl.AsXmlNode();
        Assert.IsTrue(Src.IsDeclaration(Node), 'IsXmlDeclaration must be true for a declaration node');
    end;

    [Test]
    procedure IsDeclaration_False_ForElement()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.IsDeclaration(Node), 'IsXmlDeclaration must be false for an element node');
    end;

    // ── IsXmlProcessingInstruction ──────────────────────────────────────────────

    [Test]
    procedure IsProcessingInstruction_True_ForPI()
    var
        PI: XmlProcessingInstruction;
        Node: XmlNode;
    begin
        PI := XmlProcessingInstruction.Create('xml-stylesheet', 'type="text/xsl"');
        Node := PI.AsXmlNode();
        Assert.IsTrue(Src.IsProcessingInstruction(Node), 'IsXmlProcessingInstruction must be true for a PI node');
    end;

    [Test]
    procedure IsProcessingInstruction_False_ForElement()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.IsProcessingInstruction(Node), 'IsXmlProcessingInstruction must be false for an element');
    end;

    // ── IsXmlDocument — XmlDocument.AsXmlNode() ─────────────────────────────────

    [Test]
    procedure IsDocument_True_ForDocument()
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Node: XmlNode;
    begin
        // Build a document programmatically to avoid XmlDocument.ReadFrom gap
        Doc := XmlDocument.Create();
        Root := XmlElement.Create('root');
        Doc.Add(Root);
        Node := Doc.AsXmlNode();
        Assert.IsTrue(Src.IsDocument(Node), 'IsXmlDocument must be true for an XmlDocument node');
    end;

    [Test]
    procedure IsDocument_False_ForElement()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('root');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.IsDocument(Node), 'IsXmlDocument must be false for an element node');
    end;

    // ── AsXmlElement ────────────────────────────────────────────────────────────

    [Test]
    procedure AsElement_ReturnsCorrectName()
    var
        Elem: XmlElement;
        Node: XmlNode;
    begin
        Elem := XmlElement.Create('myelem');
        Node := Elem.AsXmlNode();
        Assert.AreEqual('myelem', Src.AsElementName(Node), 'AsXmlElement.Name must match the element name');
    end;

    [Test]
    procedure AsElement_ThrowsForAttribute()
    var
        Attr: XmlAttribute;
        Node: XmlNode;
    begin
        Attr := XmlAttribute.Create('id', 'val');
        Node := Attr.AsXmlNode();
        asserterror Src.AsElementWrongType(Node);
        Assert.ExpectedError('');
    end;

    // ── AsXmlAttribute ──────────────────────────────────────────────────────────

    [Test]
    procedure AsAttribute_ReturnsCorrectName()
    var
        Attr: XmlAttribute;
        Node: XmlNode;
    begin
        Attr := XmlAttribute.Create('myattr', 'myval');
        Node := Attr.AsXmlNode();
        Assert.AreEqual('myattr', Src.AsAttributeName(Node), 'AsXmlAttribute.Name must match the attribute name');
    end;

    // ── AsXmlText ───────────────────────────────────────────────────────────────

    [Test]
    procedure AsText_ReturnsCorrectValue()
    var
        Txt: XmlText;
        Node: XmlNode;
    begin
        Txt := XmlText.Create('textval');
        Node := Txt.AsXmlNode();
        Assert.AreEqual('textval', Src.AsTextValue(Node), 'AsXmlText.Value must match the text value');
    end;

    // ── AsXmlCData ──────────────────────────────────────────────────────────────

    [Test]
    procedure AsCData_ReturnsCorrectValue()
    var
        CData: XmlCData;
        Node: XmlNode;
    begin
        CData := XmlCData.Create('rawdata');
        Node := CData.AsXmlNode();
        Assert.AreEqual('rawdata', Src.AsCDataValue(Node), 'AsXmlCData.Value must match the CDATA value');
    end;

    // ── AsXmlComment ────────────────────────────────────────────────────────────

    [Test]
    procedure AsComment_ReturnsCorrectValue()
    var
        Comment: XmlComment;
        Node: XmlNode;
    begin
        Comment := XmlComment.Create('my comment');
        Node := Comment.AsXmlNode();
        Assert.AreEqual('my comment', Src.AsCommentValue(Node), 'AsXmlComment.Value must match the comment');
    end;

    // ── AsXmlDeclaration ────────────────────────────────────────────────────────

    [Test]
    procedure AsDeclaration_ReturnsVersion()
    var
        Decl: XmlDeclaration;
        Node: XmlNode;
    begin
        Decl := XmlDeclaration.Create('1.0', 'utf-8', '');
        Node := Decl.AsXmlNode();
        Assert.AreEqual('1.0', Src.AsDeclarationVersion(Node), 'AsXmlDeclaration.Version must return 1.0');
    end;

    // ── AsXmlProcessingInstruction ──────────────────────────────────────────────

    [Test]
    procedure AsProcessingInstruction_ReturnsTarget()
    var
        PI: XmlProcessingInstruction;
        Node: XmlNode;
    begin
        PI := XmlProcessingInstruction.Create('xml-stylesheet', 'type="text/xsl"');
        Node := PI.AsXmlNode();
        Assert.AreEqual('xml-stylesheet', Src.AsProcessingInstructionTarget(Node), 'AsXmlProcessingInstruction.Target must match');
    end;

    // ── WriteTo ─────────────────────────────────────────────────────────────────

    [Test]
    procedure WriteTo_ContainsElementName()
    var
        Elem: XmlElement;
        Node: XmlNode;
        Result: Text;
    begin
        Elem := XmlElement.Create('widget');
        Node := Elem.AsXmlNode();
        Result := Src.WriteNodeToText(Node);
        Assert.IsTrue(Result.Contains('widget'), 'WriteTo must serialize the element name');
    end;

    [Test]
    procedure WriteTo_AttributeNode_ContainsValue()
    var
        Attr: XmlAttribute;
        Node: XmlNode;
        Result: Text;
    begin
        Attr := XmlAttribute.Create('color', 'blue');
        Node := Attr.AsXmlNode();
        Result := Src.WriteNodeToText(Node);
        Assert.IsTrue(Result.Contains('blue'), 'WriteTo of an attribute node must contain its value');
    end;

    // ── GetParent ───────────────────────────────────────────────────────────────

    [Test]
    procedure GetParent_True_ForChildNode()
    var
        Root: XmlElement;
        Child: XmlElement;
        Node: XmlNode;
        Parent: XmlElement;
    begin
        Root := XmlElement.Create('root');
        Child := XmlElement.Create('child');
        Root.Add(Child);
        Node := Child.AsXmlNode();
        Assert.IsTrue(Src.NodeGetParent(Node, Parent), 'GetParent must return true for a parented child node');
    end;

    [Test]
    procedure GetParent_False_ForOrphanNode()
    var
        Elem: XmlElement;
        Node: XmlNode;
        Parent: XmlElement;
    begin
        Elem := XmlElement.Create('orphan');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.NodeGetParent(Node, Parent), 'GetParent must return false for a node with no parent');
    end;

    // ── GetDocument ─────────────────────────────────────────────────────────────

    [Test]
    procedure GetDocument_True_ForDocumentChild()
    var
        Doc: XmlDocument;
        Root: XmlElement;
        Node: XmlNode;
        FoundDoc: XmlDocument;
    begin
        Doc := XmlDocument.Create();
        Root := XmlElement.Create('root');
        Doc.Add(Root);
        Node := Root.AsXmlNode();
        Assert.IsTrue(Src.NodeGetDocument(Node, FoundDoc), 'GetDocument must return true for a node in a document');
    end;

    [Test]
    procedure GetDocument_False_ForOrphanNode()
    var
        Elem: XmlElement;
        Node: XmlNode;
        Doc: XmlDocument;
    begin
        Elem := XmlElement.Create('orphan');
        Node := Elem.AsXmlNode();
        Assert.IsFalse(Src.NodeGetDocument(Node, Doc), 'GetDocument must return false for an orphan node');
    end;

    // ── SelectNodes ─────────────────────────────────────────────────────────────

    [Test]
    procedure SelectNodes_FindsTwoChildren()
    var
        Root: XmlElement;
    begin
        Root := Src.BuildTwoChildTree();
        Assert.AreEqual(2, Src.NodeSelectNodesCount(Root, 'item'), 'SelectNodes must find 2 item children');
    end;

    [Test]
    procedure SelectNodes_NoMatch_ReturnsZeroCount()
    var
        Root: XmlElement;
    begin
        Root := Src.BuildTwoChildTree();
        Assert.AreEqual(0, Src.NodeSelectNodesCount(Root, 'zzz'), 'SelectNodes with no match must return 0');
    end;

    // ── SelectSingleNode ────────────────────────────────────────────────────────

    [Test]
    procedure SelectSingleNode_ReturnsTrue_ForMatch()
    var
        Root: XmlElement;
    begin
        Root := Src.BuildTwoChildTree();
        Assert.IsTrue(Src.NodeSelectSingleNodeFound(Root, 'item'), 'SelectSingleNode must return true for matching xpath');
    end;

    [Test]
    procedure SelectSingleNode_ReturnsFalse_ForNoMatch()
    var
        Root: XmlElement;
    begin
        Root := Src.BuildTwoChildTree();
        Assert.IsFalse(Src.NodeSelectSingleNodeFound(Root, 'zzz'), 'SelectSingleNode must return false for no-match');
    end;

    // ── Remove ──────────────────────────────────────────────────────────────────

    [Test]
    procedure Remove_DecrementsParentChildCount()
    var
        Root: XmlElement;
        Child: XmlElement;
        Node: XmlNode;
    begin
        Root := XmlElement.Create('root');
        Child := XmlElement.Create('child');
        Root.Add(Child);
        Node := Child.AsXmlNode();
        Src.RemoveNodeFromParent(Node);
        Assert.AreEqual(0, Root.GetChildNodes().Count(), 'Remove must detach the node from its parent');
    end;

    // ── ReplaceWith ─────────────────────────────────────────────────────────────

    [Test]
    procedure ReplaceWith_SubstitutesNode()
    var
        Root: XmlElement;
        OldChild: XmlElement;
        NewChild: XmlElement;
        OldNode: XmlNode;
        NewNode: XmlNode;
        Nodes: XmlNodeList;
        Result: XmlNode;
        ResultElem: XmlElement;
    begin
        Root := XmlElement.Create('root');
        OldChild := XmlElement.Create('old');
        Root.Add(OldChild);
        NewChild := XmlElement.Create('new');
        OldNode := OldChild.AsXmlNode();
        NewNode := NewChild.AsXmlNode();
        Src.ReplaceNodeWith(OldNode, NewNode);
        Nodes := Root.GetChildNodes();
        Nodes.Get(1, Result);
        ResultElem := Result.AsXmlElement();
        Assert.AreEqual('new', ResultElem.Name, 'ReplaceWith must substitute the old node with the new one');
    end;

    // ── AddAfterSelf ────────────────────────────────────────────────────────────

    [Test]
    procedure AddAfterSelf_PlacesNodeAfter()
    var
        Root: XmlElement;
        First: XmlElement;
        Second: XmlElement;
        FirstNode: XmlNode;
        SecondNode: XmlNode;
        Nodes: XmlNodeList;
        N: XmlNode;
        NE: XmlElement;
    begin
        Root := XmlElement.Create('root');
        First := XmlElement.Create('first');
        Root.Add(First);
        Second := XmlElement.Create('second');
        FirstNode := First.AsXmlNode();
        SecondNode := Second.AsXmlNode();
        Src.AddAfterSelfNode(FirstNode, SecondNode);
        Nodes := Root.GetChildNodes();
        Nodes.Get(2, N);
        NE := N.AsXmlElement();
        Assert.AreEqual('second', NE.Name, 'AddAfterSelf must insert the node immediately after the reference node');
    end;

    // ── AddBeforeSelf ───────────────────────────────────────────────────────────

    [Test]
    procedure AddBeforeSelf_PlacesNodeBefore()
    var
        Root: XmlElement;
        First: XmlElement;
        Second: XmlElement;
        FirstNode: XmlNode;
        SecondNode: XmlNode;
        Nodes: XmlNodeList;
        N: XmlNode;
        NE: XmlElement;
    begin
        Root := XmlElement.Create('root');
        First := XmlElement.Create('first');
        Root.Add(First);
        Second := XmlElement.Create('second');
        FirstNode := First.AsXmlNode();
        SecondNode := Second.AsXmlNode();
        Src.AddBeforeSelfNode(FirstNode, SecondNode);
        Nodes := Root.GetChildNodes();
        Nodes.Get(1, N);
        NE := N.AsXmlElement();
        Assert.AreEqual('second', NE.Name, 'AddBeforeSelf must insert the node immediately before the reference node');
    end;
}
