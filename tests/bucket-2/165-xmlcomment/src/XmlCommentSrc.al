/// Helper codeunit exercising XmlComment.
codeunit 59780 "XCM Src"
{
    procedure CreateAndGetValue(text: Text): Text
    var
        c: XmlComment;
    begin
        c := XmlComment.Create(text);
        exit(c.Value);
    end;

    procedure CreateAsXmlNode(text: Text): XmlNode
    var
        c: XmlComment;
    begin
        c := XmlComment.Create(text);
        exit(c.AsXmlNode());
    end;

    procedure AttachToElement(text: Text): Integer
    var
        root: XmlElement;
        c: XmlComment;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);

        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // GetParent — returns true once attached to an element
    procedure GetParentAfterAttach(text: Text): Boolean
    var
        root: XmlElement;
        c: XmlComment;
        parent: XmlElement;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);
        exit(c.GetParent(parent));
    end;

    // Remove — child count drops back to 0 after Remove
    procedure RemoveFromParent(text: Text): Integer
    var
        root: XmlElement;
        c: XmlComment;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);
        c.Remove();
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // AddAfterSelf — parent should have 2 children
    procedure AddAfterSelf(text: Text): Integer
    var
        root: XmlElement;
        c: XmlComment;
        sibling: XmlComment;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);
        sibling := XmlComment.Create('sibling');
        c.AddAfterSelf(sibling);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // AddBeforeSelf — parent should have 2 children
    procedure AddBeforeSelf(text: Text): Integer
    var
        root: XmlElement;
        c: XmlComment;
        sibling: XmlComment;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);
        sibling := XmlComment.Create('before');
        c.AddBeforeSelf(sibling);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // GetDocument — returns true when node belongs to an XmlDocument
    procedure GetDocument(text: Text): Boolean
    var
        doc: XmlDocument;
        root: XmlElement;
        c: XmlComment;
        outDoc: XmlDocument;
    begin
        doc := XmlDocument.Create();
        root := XmlElement.Create('root');
        doc.Add(root);
        c := XmlComment.Create(text);
        root.Add(c);
        exit(c.GetDocument(outDoc));
    end;

    // ReplaceWith — parent child count stays 1 after replacement
    procedure ReplaceWith(text: Text): Integer
    var
        root: XmlElement;
        c: XmlComment;
        replacement: XmlElement;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);
        replacement := XmlElement.Create('replaced');
        c.ReplaceWith(replacement);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // WriteTo(var Text) — output must contain the comment text
    procedure WriteToText(text: Text): Text
    var
        c: XmlComment;
        result: Text;
    begin
        c := XmlComment.Create(text);
        c.WriteTo(result);
        exit(result);
    end;

    // SelectNodes — returns node list (non-negative count)
    procedure SelectNodesCount(text: Text): Integer
    var
        root: XmlElement;
        c: XmlComment;
        nodeList: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);
        c.SelectNodes('comment()', nodeList);
        exit(nodeList.Count);
    end;

    // SelectSingleNode — returns boolean result
    procedure SelectSingleNodeFound(text: Text): Boolean
    var
        root: XmlElement;
        c: XmlComment;
        result: XmlNode;
    begin
        root := XmlElement.Create('root');
        c := XmlComment.Create(text);
        root.Add(c);
        exit(c.SelectSingleNode('comment()', result));
    end;
}
