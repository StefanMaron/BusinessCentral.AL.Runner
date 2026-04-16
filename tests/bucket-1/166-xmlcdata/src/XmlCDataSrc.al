/// Helper codeunit exercising XmlCData — all 12 methods from issue #706.
codeunit 90000 "XCD Src"
{
    // Create + Value
    procedure CreateAndGetValue(rawText: Text): Text
    var
        cd: XmlCData;
    begin
        cd := XmlCData.Create(rawText);
        exit(cd.Value());
    end;

    // AsXmlNode
    procedure CreateAsXmlNode(rawText: Text): XmlNode
    var
        cd: XmlCData;
    begin
        cd := XmlCData.Create(rawText);
        exit(cd.AsXmlNode());
    end;

    // Add to element, count children
    procedure AttachToElement(rawText: Text): Integer
    var
        root: XmlElement;
        cd: XmlCData;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // WriteTo(var Text)
    procedure WriteToText(rawText: Text): Text
    var
        cd: XmlCData;
        result: Text;
    begin
        cd := XmlCData.Create(rawText);
        cd.WriteTo(result);
        exit(result);
    end;

    // GetParent
    procedure GetParentAfterAttach(rawText: Text): Boolean
    var
        root: XmlElement;
        cd: XmlCData;
        parent: XmlElement;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        exit(cd.GetParent(parent));
    end;

    // Remove
    procedure RemoveFromParent(rawText: Text): Integer
    var
        root: XmlElement;
        cd: XmlCData;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        cd.Remove();
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // AddAfterSelf — cd is added to root first, then a sibling is added after it.
    // Result: root should have 2 children.
    procedure AddAfterSelf(rawText: Text): Integer
    var
        root: XmlElement;
        cd: XmlCData;
        sibling: XmlCData;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        sibling := XmlCData.Create('sibling');
        cd.AddAfterSelf(sibling);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // AddBeforeSelf — cd is added to root first, then a sibling is added before it.
    // Result: root should have 2 children; sibling is at index 0.
    procedure AddBeforeSelf(rawText: Text): Integer
    var
        root: XmlElement;
        cd: XmlCData;
        sibling: XmlCData;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        sibling := XmlCData.Create('before');
        cd.AddBeforeSelf(sibling);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // GetDocument — attach cdata to an element in an XmlDocument; GetDocument must succeed.
    procedure GetDocument(rawText: Text): Boolean
    var
        doc: XmlDocument;
        root: XmlElement;
        cd: XmlCData;
        outDoc: XmlDocument;
    begin
        doc := XmlDocument.Create();
        root := XmlElement.Create('root');
        doc.Add(root);
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        exit(cd.GetDocument(outDoc));
    end;

    // ReplaceWith — replace cd with a comment; parent child count stays 1.
    procedure ReplaceWith(rawText: Text): Integer
    var
        root: XmlElement;
        cd: XmlCData;
        replacement: XmlComment;
        nodes: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        replacement := XmlComment.Create('replaced');
        cd.ReplaceWith(replacement);
        nodes := root.GetChildNodes();
        exit(nodes.Count);
    end;

    // SelectNodes — xpath on parent; returns node list count.
    procedure SelectNodesCount(rawText: Text): Integer
    var
        root: XmlElement;
        cd: XmlCData;
        nodeList: XmlNodeList;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        // Select the text() nodes under root (CDATA counts as text node in XPath)
        cd.SelectNodes('text()', nodeList);
        exit(nodeList.Count);
    end;

    // SelectSingleNode — find the parent element by ancestor axis.
    procedure SelectSingleNodeFound(rawText: Text): Boolean
    var
        root: XmlElement;
        cd: XmlCData;
        result: XmlNode;
    begin
        root := XmlElement.Create('root');
        cd := XmlCData.Create(rawText);
        root.Add(cd);
        exit(cd.SelectSingleNode('text()', result));
    end;
}
